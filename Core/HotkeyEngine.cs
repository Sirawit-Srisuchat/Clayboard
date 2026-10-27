using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MacroInWZa007.Actions;
using MacroInWZa007.Input;

namespace MacroInWZa007.Core;

/// <summary>
/// Receives key capture progress. <paramref name="vk"/> is 0 while only modifiers are held;
/// <paramref name="done"/> is true once every key has been released.
/// </summary>
public delegate void CaptureHandler(Mods mods, int vk, bool done);

/// <summary>
/// Matches key presses from the hook against bindings and fires their actions.
/// The hook and all matching state live on a dedicated high-priority thread (see <see cref="Create"/>),
/// so a busy UI never delays typing anywhere on the PC. Public members can be called from any thread.
/// </summary>
public sealed class HotkeyEngine : IDisposable
{
    public const int DefaultSequenceTimeoutMs = 1500;
    public const int MinSequenceTimeoutMs = 200;
    public const int MaxSequenceTimeoutMs = 5000;
    private const int DoubleTapMs = 400;

    private sealed record Entry(TriggerSpec Trigger, HotkeyBinding Binding, string? App);

    private sealed class HoldState(Entry entry, int vk, bool swallowed)
    {
        public Entry Entry { get; } = entry;
        public int Vk { get; } = vk;
        public bool Swallowed { get; } = swallowed;
        public bool Fired { get; set; }
    }

    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHook _hook;
    private readonly ForegroundTracker _foreground = new();
    private readonly DispatcherTimer _holdTimer = new();

    // Bindings by their first combo, so a key press is one lookup with no allocation.
    // Within each list, app-specific bindings come first so they win over global ones.
    private Dictionary<KeyCombo, Entry[]> _byFirstCombo = new();
    private readonly List<Entry> _candidates = new();

    // Physical keys that are down, to tell auto-repeat apart from new presses.
    private readonly HashSet<int> _down = new();

    // Main keys of hotkeys that fired and are still held, with whether we swallowed them.
    // Lets us ignore auto-repeat and swallow the matching key-up as well.
    private readonly Dictionary<int, bool> _heldKeys = new();

    // Sequence in progress: bindings whose first steps matched, and how many steps did.
    private List<Entry> _sequence = new();
    private int _sequenceIndex;
    private long _sequenceDeadline;
    private readonly DispatcherTimer _sequenceTimer = new();

    // Keys held back while waiting to see whether a sequence continues. If it doesn't,
    // they're typed after all, so keys that start a sequence still work on their own.
    private readonly List<KeyCombo> _sequenceHeldBack = new();
    private bool _replayedForThisKey;

    // Last press, for double-taps. Lone modifiers are tracked apart from other keys, because
    // re-pressing Ctrl+Alt between two taps of Ctrl+Alt+K must not break the double-tap.
    private KeyCombo? _lastPress;
    private long _lastPressTime;
    private KeyCombo? _lastModifierPress;
    private long _lastModifierPressTime;

    private HoldState? _hold;

    // Key capture for the "record keys" box: while active, every key goes to it instead of apps.
    private CaptureHandler? _capture;
    private readonly HashSet<int> _captureHeld = new();
    private int _captureVk;
    private Mods _captureMods;
    private Mods _capturePeakMods;
    private Mods _captureLastMod;

    private volatile bool _paused;

    private HotkeyEngine()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hook = new KeyboardHook(OnKey);
        _holdTimer.Tick += (_, _) => OnHoldElapsed();
        _sequenceTimer.Tick += (_, _) => ReplayHeldBackKeys();
    }

    /// <summary>Creates the engine on its own thread, which then runs the hook's message loop.</summary>
    public static HotkeyEngine Create()
    {
        HotkeyEngine? engine = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            engine = new HotkeyEngine();
            ready.Set();
            Dispatcher.Run();
        })
        {
            Name = "Keyboard hook",
            IsBackground = true,
            // Every keystroke on the PC waits for this thread, so it must never queue behind other work.
            Priority = ThreadPriority.Highest,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return engine!;
    }

    private volatile int _sequenceTimeoutMs = DefaultSequenceTimeoutMs;

    /// <summary>How long to wait for the next step of a sequence before typing the held-back keys.</summary>
    public int SequenceTimeoutMs
    {
        get => _sequenceTimeoutMs;
        set => _sequenceTimeoutMs = value;
    }

    public event Action<HotkeyBinding, Exception>? ActionFailed;

    /// <summary>Raised on the hook thread when Ctrl+Alt+Shift+Pause is pressed: the emergency on/off switch.</summary>
    public event Action? EmergencyToggle;

    public bool Paused
    {
        get => _paused;
        set => OnHookThread(() =>
        {
            _paused = value;
            ResetPending();
        });
    }

    public void Start() => OnHookThread(() =>
    {
        _foreground.Start();
        _hook.Install();
    });

    /// <summary>Replaces the active bindings.</summary>
    public void SetBindings(IEnumerable<(TriggerSpec Trigger, HotkeyBinding Binding)> bindings)
    {
        var lookup = bindings
            .Select(b => new Entry(b.Trigger, b.Binding,
                string.IsNullOrWhiteSpace(b.Binding.App) ? null : ForegroundTracker.Normalize(b.Binding.App)))
            .GroupBy(e => e.Trigger.First)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.App is null).ToArray());
        OnHookThread(() =>
        {
            _byFirstCombo = lookup;
            ResetPending();
        });
    }

    /// <summary>
    /// Routes all keys to <paramref name="handler"/> (and away from other apps) until the user
    /// releases a full combo or <see cref="EndCapture"/> is called. The handler runs on the hook thread.
    /// </summary>
    public void BeginCapture(CaptureHandler handler) => OnHookThread(() =>
    {
        EndCaptureCore();
        ResetPending();
        _capture = handler;
    });

    public void EndCapture() => OnHookThread(EndCaptureCore);

    private void OnHookThread(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.Invoke(action);
    }

    private void EndCaptureCore()
    {
        if (_capture is null)
            return;
        _capture = null;
        // Keys still down had their key-down swallowed, so swallow their key-up too.
        foreach (int vk in _captureHeld)
            _heldKeys[vk] = true;
        _captureHeld.Clear();
        _captureVk = 0;
        _captureMods = _capturePeakMods = _captureLastMod = Mods.None;
    }

    public void Dispose()
    {
        OnHookThread(() =>
        {
            _hook.Dispose();
            _foreground.Dispose();
            _holdTimer.Stop();
            _sequenceTimer.Stop();
        });
        _dispatcher.InvokeShutdown();
    }

    private bool OnKey(int vk, bool isDown)
    {
        bool isRepeat = isDown && !_down.Add(vk);
        if (!isDown)
            _down.Remove(vk);

        if (_capture is not null)
            return OnCaptureKey(_capture, vk, isDown);
        if (!isDown)
            return OnKeyUp(vk);

        if (_hold?.Vk == vk)
            return _hold.Swallowed;
        if (_heldKeys.TryGetValue(vk, out bool repeatSwallowed))
            return repeatSwallowed;
        if (isRepeat)
            return false;

        var combo = ComboFor(vk);
        if (IsEmergencyToggle(combo))
        {
            // Works even while paused, so a binding that eats important keys can always be switched off.
            EmergencyToggle?.Invoke();
            return Remember(vk, true);
        }
        if (_paused)
            return false;

        // Pressing anything else means the user isn't just holding a key.
        if (_hold is not null)
            CancelHold();

        long now = Environment.TickCount64;
        string app = _foreground.CurrentApp;

        _replayedForThisKey = false;
        if (_sequence.Count > 0 && ContinueSequence(combo, vk, now, out bool swallowed))
            return swallowed;

        bool swallowFresh = HandleFreshPress(combo, vk, now, app);
        if (!swallowFresh && _replayedForThisKey)
        {
            // The held-back keys were just re-sent and are queued behind this key. Send this one
            // after them instead, so "1" then "5" still types "15" and not "51".
            InputSender.SendKeyDown(vk);
            return true;
        }
        return swallowFresh;
    }

    // Ctrl+Pause arrives as VK_CANCEL (Break) on most keyboards, so accept either.
    private static bool IsEmergencyToggle(KeyCombo combo) =>
        combo.Mods == (Mods.Ctrl | Mods.Alt | Mods.Shift) && combo.Vk is 0x13 or 0x03;

    private bool HandleFreshPress(KeyCombo combo, int vk, long now, string app)
    {
        bool isDoubleTap = RecordPress(combo, vk, now);
        if (!_byFirstCombo.TryGetValue(combo, out var entries))
            return false;

        // Most presses match nothing and return above; the rest pick by mode without allocating.
        _candidates.Clear();
        foreach (var entry in entries)
            if (entry.App is null || entry.App == app)
                _candidates.Add(entry);

        if (isDoubleTap && Find(TriggerMode.DoubleTap, sequence: false) is { } doubleTap)
        {
            // A third tap starts over instead of firing again.
            _lastPress = _lastModifierPress = null;
            return Fire(doubleTap.Binding, combo, vk);
        }

        if (Find(TriggerMode.Hold, sequence: false) is { } hold)
        {
            StartHold(hold, vk);
            return _hold!.Swallowed;
        }

        if (Find(TriggerMode.Press, sequence: false) is { } press)
            return Fire(press.Binding, combo, vk);

        var sequences = _candidates.Where(e => e.Trigger.Mode == TriggerMode.Press && e.Trigger.IsSequence).ToList();
        if (sequences.Count > 0)
        {
            _sequence = sequences;
            _sequenceIndex = 1;
            _sequenceHeldBack.Clear();
            HoldBack(combo, now);
            return Swallow(combo, vk);
        }
        return false;
    }

    private Entry? Find(TriggerMode mode, bool sequence)
    {
        foreach (var entry in _candidates)
            if (entry.Trigger.Mode == mode && entry.Trigger.IsSequence == sequence)
                return entry;
        return null;
    }

    private void HoldBack(KeyCombo combo, long now)
    {
        _sequenceHeldBack.Add(combo);
        _sequenceDeadline = now + SequenceTimeoutMs;
        _sequenceTimer.Stop();
        _sequenceTimer.Interval = TimeSpan.FromMilliseconds(SequenceTimeoutMs);
        _sequenceTimer.Start();
    }

    /// <summary>The sequence didn't happen: type the keys that were held back while waiting for it.</summary>
    private void ReplayHeldBackKeys()
    {
        _sequenceTimer.Stop();
        _sequence.Clear();
        if (_sequenceHeldBack.Count == 0)
            return;
        var held = InputSender.CurrentMods();
        foreach (var combo in _sequenceHeldBack)
        {
            // Modifiers still physically down already apply; only press the ones that were let go.
            if ((combo.Mods & ~held) == Mods.None)
                InputSender.SendKey(combo.Vk);
            else
                InputSender.SendCombo(combo);
        }
        _sequenceHeldBack.Clear();
        _replayedForThisKey = true;
    }

    /// <summary>Remembers a press and says whether it repeats the previous one quickly enough to be a double-tap.</summary>
    private bool RecordPress(KeyCombo combo, int vk, long now)
    {
        if (KeyCombo.IsModifierVk(vk))
        {
            bool repeated = _lastModifierPress == combo && now - _lastModifierPressTime <= DoubleTapMs;
            _lastModifierPress = combo;
            _lastModifierPressTime = now;
            return repeated;
        }

        _lastModifierPress = null; // Shift, A, Shift is typing, not a double-tap of Shift.
        bool isRepeat = _lastPress == combo && now - _lastPressTime <= DoubleTapMs;
        _lastPress = combo;
        _lastPressTime = now;
        return isRepeat;
    }

    private bool OnKeyUp(int vk)
    {
        if (_hold?.Vk == vk)
        {
            var hold = _hold;
            _hold = null;
            _holdTimer.Stop();
            // Released before the hold time: give the app the tap it would normally have seen.
            if (hold.Swallowed && !hold.Fired)
                InputSender.SendKey(vk);
            return hold.Swallowed;
        }
        return _heldKeys.Remove(vk, out bool swallowed) && swallowed;
    }

    /// <summary>Feeds a key to the sequence in progress. Returns false to handle the key as a fresh press.</summary>
    private bool ContinueSequence(KeyCombo combo, int vk, long now, out bool swallowed)
    {
        swallowed = false;
        if (now > _sequenceDeadline)
        {
            ReplayHeldBackKeys();
            return false;
        }
        if (KeyCombo.IsModifierVk(vk))
            return true; // Modifiers for the next step: let them through and keep waiting.

        var matching = _sequence.Where(e => e.Trigger.Steps[_sequenceIndex] == combo).ToList();
        if (matching.Count == 0)
        {
            ReplayHeldBackKeys();
            return false;
        }

        _sequenceIndex++;
        if (matching.FirstOrDefault(e => e.Trigger.Steps.Count == _sequenceIndex) is { } complete)
        {
            _sequence.Clear();
            _sequenceHeldBack.Clear();
            _sequenceTimer.Stop();
            swallowed = Fire(complete.Binding, combo, vk);
            return true;
        }

        _sequence = matching;
        HoldBack(combo, now);
        swallowed = Swallow(combo, vk);
        return true;
    }

    private void StartHold(Entry entry, int vk)
    {
        _hold = new HoldState(entry, vk, swallowed: entry.Binding.Suppress && !KeyCombo.IsModifierVk(vk));
        _holdTimer.Interval = TimeSpan.FromMilliseconds(entry.Trigger.HoldMs);
        _holdTimer.Start();
    }

    private void OnHoldElapsed()
    {
        _holdTimer.Stop();
        if (_hold is null || _hold.Fired)
            return;
        _hold.Fired = true;
        Run(_hold.Entry.Binding);
    }

    private void CancelHold()
    {
        var hold = _hold!;
        _hold = null;
        _holdTimer.Stop();
        if (!hold.Swallowed)
            return;
        if (hold.Fired)
            _heldKeys[hold.Vk] = true; // The action ran; keep hiding the key until it's released.
        else
            InputSender.SendKeyDown(hold.Vk); // Hand back the key-down we held on to; its key-up will pass.
    }

    private void ResetPending()
    {
        _sequence.Clear();
        _sequenceHeldBack.Clear();
        _sequenceTimer.Stop();
        _lastPress = _lastModifierPress = null;
        if (_hold is not null)
            CancelHold();
    }

    private bool Fire(HotkeyBinding binding, KeyCombo combo, int vk)
    {
        Run(binding);
        // Swallowing a modifier would leave apps thinking it's stuck down.
        return binding.Suppress && !KeyCombo.IsModifierVk(vk) ? Swallow(combo, vk) : Remember(vk, false);
    }

    private bool Swallow(KeyCombo combo, int vk)
    {
        if ((combo.Mods & (Mods.Alt | Mods.Win)) != 0)
            InputSender.SendMaskKey();
        return Remember(vk, true);
    }

    private bool Remember(int vk, bool swallowed)
    {
        _heldKeys[vk] = swallowed;
        return swallowed;
    }

    private static KeyCombo ComboFor(int vk)
    {
        var mods = InputSender.CurrentMods();
        var mod = KeyCombo.ModifierOf(vk);
        return mod == Mods.None
            ? new KeyCombo(mods, vk)
            : new KeyCombo(mods & ~mod, KeyCombo.GenericVk(mod));
    }

    private bool OnCaptureKey(CaptureHandler handler, int vk, bool isDown)
    {
        if (isDown)
        {
            _captureHeld.Add(vk);
            if (_captureVk == 0)
            {
                var mod = KeyCombo.ModifierOf(vk);
                if (mod != Mods.None)
                {
                    _captureLastMod = mod;
                    _capturePeakMods |= mod;
                    handler(HeldMods(), 0, false);
                }
                else
                {
                    _captureVk = vk;
                    _captureMods = HeldMods();
                    handler(_captureMods, vk, false);
                }
            }
            return true;
        }

        // A key that was already down when capture began: let its key-up through as usual.
        if (!_captureHeld.Remove(vk))
            return _heldKeys.Remove(vk, out bool swallowed) && swallowed;

        if (_captureVk == 0)
        {
            if (_captureHeld.Count == 0 && _captureLastMod != Mods.None)
            {
                // Only modifiers were pressed: record the last one as the key, e.g. "Shift" for a double-tap.
                var (mods, mainVk) = (_capturePeakMods & ~_captureLastMod, KeyCombo.GenericVk(_captureLastMod));
                EndCaptureCore();
                handler(mods, mainVk, true);
            }
            else
            {
                handler(HeldMods(), 0, false);
            }
        }
        else if (_captureHeld.Count == 0)
        {
            var (mods, mainVk) = (_captureMods, _captureVk);
            EndCaptureCore();
            handler(mods, mainVk, true);
        }
        return true;
    }

    // Swallowed keys never reach the async key state, so track modifiers from our own events.
    private Mods HeldMods() =>
        _captureHeld.Aggregate(Mods.None, (mods, vk) => mods | KeyCombo.ModifierOf(vk));

    private void Run(HotkeyBinding binding)
    {
        // Never run actions on the hook thread: Windows drops hooks that respond slowly.
        Task.Run(() =>
        {
            try
            {
                ActionRunner.Run(binding.Action);
            }
            catch (Exception ex)
            {
                ActionFailed?.Invoke(binding, ex);
            }
        });
    }
}
