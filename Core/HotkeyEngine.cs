using System;
using System.Collections.Generic;
using System.Linq;
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
/// Everything except action execution runs on the UI thread, which also runs the hook.
/// </summary>
public sealed class HotkeyEngine : IDisposable
{
    private const int SequenceTimeoutMs = 1500;
    private const int DoubleTapMs = 400;

    private sealed record Entry(TriggerSpec Trigger, HotkeyBinding Binding, string? App);

    private sealed class HoldState(Entry entry, int vk, bool swallowed)
    {
        public Entry Entry { get; } = entry;
        public int Vk { get; } = vk;
        public bool Swallowed { get; } = swallowed;
        public bool Fired { get; set; }
    }

    private readonly KeyboardHook _hook;
    private readonly ForegroundTracker _foreground = new();
    private readonly DispatcherTimer _holdTimer = new();

    // App-specific bindings come first so they win over global ones with the same keys.
    private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();

    // Physical keys that are down, to tell auto-repeat apart from new presses.
    private readonly HashSet<int> _down = new();

    // Main keys of hotkeys that fired and are still held, with whether we swallowed them.
    // Lets us ignore auto-repeat and swallow the matching key-up as well.
    private readonly Dictionary<int, bool> _heldKeys = new();

    // Sequence in progress: bindings whose first steps matched, and how many steps did.
    private List<Entry> _sequence = new();
    private int _sequenceIndex;
    private long _sequenceDeadline;

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

    public HotkeyEngine()
    {
        _hook = new KeyboardHook(OnKey);
        _holdTimer.Tick += (_, _) => OnHoldElapsed();
    }

    public event Action<HotkeyBinding, Exception>? ActionFailed;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            ResetPending();
        }
    }

    public void Start()
    {
        _foreground.Start();
        _hook.Install();
    }

    /// <summary>Replaces the active bindings. Call on the UI thread.</summary>
    public void SetBindings(IEnumerable<(TriggerSpec Trigger, HotkeyBinding Binding)> bindings)
    {
        _entries = bindings
            .Select(b => new Entry(b.Trigger, b.Binding,
                string.IsNullOrWhiteSpace(b.Binding.App) ? null : ForegroundTracker.Normalize(b.Binding.App)))
            .OrderBy(e => e.App is null)
            .ToList();
        ResetPending();
    }

    /// <summary>
    /// Routes all keys to <paramref name="handler"/> (and away from other apps) until the user
    /// releases a full combo or <see cref="EndCapture"/> is called. Call on the UI thread.
    /// </summary>
    public void BeginCapture(CaptureHandler handler)
    {
        EndCapture();
        ResetPending();
        _capture = handler;
    }

    public void EndCapture()
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
        _hook.Dispose();
        _foreground.Dispose();
        _holdTimer.Stop();
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
        if (isRepeat || _paused)
            return false;

        // Pressing anything else means the user isn't just holding a key.
        if (_hold is not null)
            CancelHold();

        var combo = ComboFor(vk);
        long now = Environment.TickCount64;
        string app = _foreground.CurrentApp;

        if (_sequence.Count > 0 && ContinueSequence(combo, vk, now, out bool swallowed))
            return swallowed;

        var candidates = _entries
            .Where(e => e.Trigger.First == combo && (e.App is null || e.App == app))
            .ToList();

        bool isDoubleTap = RecordPress(combo, vk, now);
        if (isDoubleTap && candidates.FirstOrDefault(e => e.Trigger.Mode == TriggerMode.DoubleTap) is { } doubleTap)
        {
            // A third tap starts over instead of firing again.
            _lastPress = _lastModifierPress = null;
            return Fire(doubleTap.Binding, combo, vk);
        }

        if (candidates.FirstOrDefault(e => e.Trigger.Mode == TriggerMode.Hold) is { } hold)
        {
            StartHold(hold, vk);
            return _hold!.Swallowed;
        }

        if (candidates.FirstOrDefault(e => e.Trigger.Mode == TriggerMode.Press && !e.Trigger.IsSequence) is { } press)
            return Fire(press.Binding, combo, vk);

        var sequences = candidates.Where(e => e.Trigger.Mode == TriggerMode.Press && e.Trigger.IsSequence).ToList();
        if (sequences.Count > 0)
        {
            _sequence = sequences;
            _sequenceIndex = 1;
            _sequenceDeadline = now + SequenceTimeoutMs;
            return Swallow(combo, vk);
        }
        return false;
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
            _sequence.Clear();
            return false;
        }
        if (KeyCombo.IsModifierVk(vk))
            return true; // Modifiers for the next step: let them through and keep waiting.

        var matching = _sequence.Where(e => e.Trigger.Steps[_sequenceIndex] == combo).ToList();
        _sequence.Clear();
        if (matching.Count == 0)
            return false;

        _sequenceIndex++;
        if (matching.FirstOrDefault(e => e.Trigger.Steps.Count == _sequenceIndex) is { } complete)
        {
            swallowed = Fire(complete.Binding, combo, vk);
            return true;
        }

        _sequence = matching;
        _sequenceDeadline = now + SequenceTimeoutMs;
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
                EndCapture();
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
            EndCapture();
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
