using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacroInWZa007.Core;

namespace MacroInWZa007.UI;

/// <summary>Box that records a key combo through the global hook when clicked.</summary>
public partial class KeyRecorder : UserControl
{
    public static readonly DependencyProperty KeysProperty = DependencyProperty.Register(
        nameof(Keys), typeof(string), typeof(KeyRecorder),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnKeysPropertyChanged)
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });

    // Only one box records at a time; starting another stops the previous one.
    private static KeyRecorder? _active;

    private readonly DispatcherTimer _idleTimer;
    private bool _recording;

    public KeyRecorder()
    {
        InitializeComponent();
        // Give up if nothing is pressed for a while, so the keyboard is never held hostage.
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _idleTimer.Tick += (_, _) => Cancel();
        Loaded += (_, _) =>
        {
            ApplyLayout();
            ShowKeys();
        };
        Unloaded += (_, _) => Cancel();
    }

    public event Action? KeysChanged;

    /// <summary>The engine whose hook does the recording; set once at startup.</summary>
    public static HotkeyEngine? Engine { get; set; }

    /// <summary>Shown when no keys are set yet.</summary>
    public string IdleHint { get; set; } = "Click to record keys";

    /// <summary>Single-line version for use inside lists.</summary>
    public bool Compact { get; set; }

    public string Keys
    {
        get => (string)GetValue(KeysProperty);
        set => SetValue(KeysProperty, value ?? "");
    }

    /// <summary>Stops whichever box is recording, if any.</summary>
    public static void CancelActive() => _active?.Cancel();

    public void Cancel()
    {
        if (!_recording)
            return;
        StopRecording();
        ShowKeys();
    }

    private static void OnKeysPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var recorder = (KeyRecorder)d;
        if (!recorder._recording && recorder.IsLoaded)
            recorder.ShowKeys();
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

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_recording)
            Cancel();
        else
            Start();
    }

    private void Start()
    {
        if (Engine is null)
            return;
        _active?.Cancel();
        _active = this;
        _recording = true;
        Caps.ItemsSource = null;
        SetStatus(Compact ? "Press keys…" : "Recording… press your keys", recording: true);
        Engine.BeginCapture(OnCapture);
        _idleTimer.Start();
    }

    private void StopRecording()
    {
        _recording = false;
        _idleTimer.Stop();
        Engine?.EndCapture();
        if (_active == this)
            _active = null;
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

        if (vk == 0)
        {
            Caps.ItemsSource = KeyCombo.ToCaps(KeyCombo.Format(mods, null));
            SetStatus(mods == Mods.None ? "Recording… press your keys" : "Add a key, or release", recording: true);
            return;
        }

        string? name = KeyCombo.KeyName(vk);
        if (name is null)
        {
            if (done)
            {
                StopRecording();
                ShowKeys();
                SetStatus("That key isn't supported. Click to try another.", recording: false);
            }
            return;
        }

        string combo = KeyCombo.Format(mods, name);
        Caps.ItemsSource = KeyCombo.ToCaps(combo);
        if (!done)
        {
            SetStatus(Compact ? "" : "Release to finish", recording: true);
            return;
        }

        StopRecording();
        Keys = combo;
        ShowKeys();
        KeysChanged?.Invoke();
    }

    private void ShowKeys()
    {
        Caps.ItemsSource = KeyCombo.ToCaps(Keys);
        string hint = Compact ? "" : "Click to change";
        SetStatus(Keys.Length == 0 ? IdleHint : hint, recording: false);
    }

    private void SetStatus(string text, bool recording)
    {
        bool hasCaps = Caps.ItemsSource is System.Collections.ICollection { Count: > 0 };
        Caps.Visibility = hasCaps ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Status.Margin = !hasCaps ? new Thickness(0) : Compact ? new Thickness(8, 0, 0, 0) : new Thickness(0, 10, 0, 0);
        Status.Text = text;
        Status.Foreground = (Brush)FindResource(recording ? "ClayBrush" : "MuteBrush");
        DashedOutline.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Box.BorderBrush = recording ? Brushes.Transparent : (Brush)FindResource("LineBrush");
    }
}
