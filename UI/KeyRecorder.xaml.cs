using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacroInWZa007.Core;
using MacroInWZa007.Localization;

namespace MacroInWZa007.UI;

/// <summary>
/// Box that records keys through the global hook when clicked. In <see cref="Multi"/> mode it records
/// a whole sequence in one go: each combo is added as it's released, and a short pause finishes it.
/// </summary>
public partial class KeyRecorder : UserControl
{
    public const int MaxSteps = 4;
    private const int FinishDelayMs = 1500;

    public static readonly DependencyProperty KeysProperty = DependencyProperty.Register(
        nameof(Keys), typeof(string), typeof(KeyRecorder),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnKeysPropertyChanged)
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });

    private static readonly CapToken Arrow = new("→", IsKey: false);

    // Only one box records at a time; starting another stops the previous one.
    private static KeyRecorder? _active;

    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _finishTimer;
    private readonly ObservableCollection<CapToken> _tokens = new();
    private readonly List<string> _steps = new();
    private bool _recording;
    private bool _multi;

    public KeyRecorder()
    {
        InitializeComponent();
        Caps.ItemsSource = _tokens;
        // Give up if nothing is pressed for a while, so the keyboard is never held hostage.
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _idleTimer.Tick += (_, _) => Cancel();
        _finishTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FinishDelayMs) };
        _finishTimer.Tick += (_, _) => Commit();
        Loaded += (_, _) =>
        {
            ApplyLayout();
            ShowKeys();
            Loc.Changed += OnLanguageChanged;
        };
        Unloaded += (_, _) =>
        {
            Cancel();
            Loc.Changed -= OnLanguageChanged;
        };
    }

    public event Action? KeysChanged;

    /// <summary>The engine whose hook does the recording; set once at startup.</summary>
    public static HotkeyEngine? Engine { get; set; }

    /// <summary>Text key (see <see cref="Loc"/>) shown when no keys are set yet.</summary>
    public string IdleHintKey { get; set; } = "Rec.Idle";

    /// <summary>Single-line version for use inside lists.</summary>
    public bool Compact { get; set; }

    /// <summary>Record a sequence of combos ("Ctrl+K, Ctrl+B") instead of a single one.</summary>
    public bool Multi
    {
        get => _multi;
        set
        {
            if (_multi == value)
                return;
            Cancel();
            _multi = value;
            if (IsLoaded)
                ShowKeys(); // The empty-box hint differs between the two modes.
        }
    }

    public string Keys
    {
        get => (string)GetValue(KeysProperty);
        set => SetValue(KeysProperty, value ?? "");
    }

    /// <summary>Stops whichever box is recording, if any.</summary>
    public static void CancelActive() => _active?.Cancel();

    /// <summary>Stops recording. A sequence keeps the steps recorded so far; anything else is discarded.</summary>
    public void Cancel()
    {
        if (!_recording)
            return;
        if (_multi && _steps.Count > 0)
        {
            Commit();
            return;
        }
        StopRecording();
        ShowKeys();
    }

    private static void OnKeysPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var recorder = (KeyRecorder)d;
        if (!recorder._recording && recorder.IsLoaded)
            recorder.ShowKeys();
    }

    private void OnLanguageChanged()
    {
        if (!_recording)
            ShowKeys();
    }

    private void ApplyLayout()
    {
        if (!Compact)
            return;
        Box.Padding = new Thickness(10, 6, 10, 6);
        Box.MinHeight = 36;
        Panel.Orientation = Orientation.Horizontal;
        Panel.HorizontalAlignment = HorizontalAlignment.Left;
        Caps.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private void OnClick(object sender, MouseButtonEventArgs e) => Toggle();

    // Enter or Space on a focused box works like a click, so recording doesn't need the mouse.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space)
        {
            Toggle();
            e.Handled = true;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (!_recording)
            Box.BorderBrush = (Brush)FindResource("ClayBrush");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!_recording)
            Box.BorderBrush = (Brush)FindResource("LineBrush");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RecorderAutomationPeer(this);

    /// <summary>Starts recording, or finishes or cancels one in progress.</summary>
    private void Toggle()
    {
        if (!_recording)
            Start();
        else if (_multi && _steps.Count > 0)
            Commit(); // Clicking is the quick way to say "that's the whole sequence".
        else
            Cancel();
    }

    /// <summary>Lets screen readers and other accessibility tools see the box as a button and press it.</summary>
    private sealed class RecorderAutomationPeer(KeyRecorder owner) : UserControlAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetNameCore() =>
            owner.Keys.Length > 0 ? owner.Keys : owner.Status.Text;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke() => owner.Toggle();
    }

    // Keycaps grow in as they're pressed, but not when a saved binding is first shown.
    private void OnCapLoaded(object sender, RoutedEventArgs e)
    {
        if (_recording)
            Motion.Pop((UIElement)sender);
    }

    private void Start()
    {
        if (Engine is null)
            return;
        _active?.Cancel();
        _active = this;
        _recording = true;
        _steps.Clear();
        SetTokens(Array.Empty<CapToken>());
        SetStatus(IdleRecordingText(), recording: true);
        Engine.BeginCapture(OnCapture);
        _idleTimer.Start();
    }

    private void StopRecording()
    {
        _recording = false;
        _idleTimer.Stop();
        _finishTimer.Stop();
        HideProgress();
        Engine?.EndCapture();
        if (_active == this)
            _active = null;
    }

    private void Commit()
    {
        if (!_recording)
            return;
        StopRecording();
        Keys = string.Join(", ", _steps);
        ShowKeys();
        KeysChanged?.Invoke();
    }

    // Called from the hook callback; defer UI work so the hook returns immediately.
    private void OnCapture(Mods mods, int vk, bool done) =>
        Dispatcher.BeginInvoke(() => ApplyCapture(mods, vk, done));

    private void ApplyCapture(Mods mods, int vk, bool done)
    {
        if (!_recording)
            return;
        _idleTimer.Stop();
        _idleTimer.Start();
        // A key is going down, so the user isn't finished: hold off the automatic finish.
        _finishTimer.Stop();
        HideProgress();

        if (vk == 0)
        {
            var held = mods == Mods.None ? null : KeyCombo.Format(mods, null);
            SetTokens(TokensFor(_steps, held));
            SetStatus(held is null ? IdleRecordingText() : Loc.T("Rec.AddKey"), recording: true);
            return;
        }

        string? name = KeyCombo.KeyName(vk);
        if (name is null)
        {
            if (done)
                RejectUnsupportedKey();
            return;
        }

        string combo = KeyCombo.Format(mods, name);
        if (!done)
        {
            SetTokens(TokensFor(_steps, combo));
            SetStatus(Compact || _multi ? "" : Loc.T("Rec.Release"), recording: true);
            return;
        }

        _steps.Add(combo);
        if (!_multi || _steps.Count >= MaxSteps)
        {
            Commit();
            return;
        }

        // Keep listening for the next step; a pause or a click ends the sequence.
        SetTokens(TokensFor(_steps, null));
        SetStatus(Loc.T("Rec.Next"), recording: true);
        Engine!.BeginCapture(OnCapture);
        _finishTimer.Start();
        ShowProgress();
    }

    private void RejectUnsupportedKey()
    {
        if (_multi && _steps.Count > 0)
        {
            // Keep the steps so far and let the user try a different key.
            SetTokens(TokensFor(_steps, null));
            SetStatus(Loc.T("Rec.Unsupported"), recording: true);
            Engine!.BeginCapture(OnCapture);
            return;
        }
        StopRecording();
        ShowKeys();
        SetStatus(Loc.T("Rec.Unsupported"), recording: false);
    }

    private string IdleRecordingText() =>
        Loc.T(_multi ? "Rec.First" : Compact ? "Rec.RecordingShort" : "Rec.Recording");

    private void ShowKeys()
    {
        SetTokens(TokensFor(TriggerSpec.SplitSteps(Keys), null));
        string hint = Compact ? "" : Loc.T("Rec.Change");
        SetStatus(Keys.Length == 0 ? Loc.T(_multi ? "Rec.IdleSequence" : IdleHintKey) : hint, recording: false);
    }

    private static List<CapToken> TokensFor(IEnumerable<string> steps, string? current)
    {
        var tokens = new List<CapToken>();
        foreach (var step in current is null ? steps : steps.Append(current))
        {
            if (tokens.Count > 0)
                tokens.Add(Arrow);
            tokens.AddRange(KeyCombo.ToCaps(step).Select(cap => new CapToken(cap, IsKey: true)));
        }
        return tokens;
    }

    /// <summary>Updates the keycaps in place, so only caps that are actually new get created (and animate).</summary>
    private void SetTokens(IReadOnlyList<CapToken> tokens)
    {
        int common = 0;
        while (common < tokens.Count && common < _tokens.Count && _tokens[common] == tokens[common])
            common++;
        while (_tokens.Count > common)
            _tokens.RemoveAt(_tokens.Count - 1);
        foreach (var token in tokens.Skip(common))
            _tokens.Add(token);
    }

    private void ShowProgress()
    {
        Progress.Visibility = Visibility.Visible;
        if (!Motion.Enabled)
            return;
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FinishDelayMs)));
    }

    private void HideProgress()
    {
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Progress.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string text, bool recording)
    {
        bool hasCaps = _tokens.Count > 0;
        Caps.Visibility = hasCaps ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Status.Margin = !hasCaps ? new Thickness(0) : Compact ? new Thickness(8, 0, 0, 0) : new Thickness(0, 10, 0, 0);
        Status.Text = text;
        Status.Foreground = (Brush)FindResource(recording ? "ClayBrush" : "MuteBrush");
        Box.BorderBrush = recording ? Brushes.Transparent : (Brush)FindResource("LineBrush");
        SetMarchingAnts(recording);
    }

    // The dashed outline creeps around while recording, so it's obvious the box is listening.
    private void SetMarchingAnts(bool on)
    {
        DashedOutline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on || !Motion.Enabled)
        {
            DashedOutline.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            return;
        }
        if (DashedOutline.HasAnimatedProperties)
            return;
        var march = new DoubleAnimation(0, -7, TimeSpan.FromMilliseconds(700)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(march, 30); // Plenty for a slow crawl, and half the work.
        DashedOutline.BeginAnimation(Shape.StrokeDashOffsetProperty, march);
    }
}
