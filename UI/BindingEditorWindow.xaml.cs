using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Clayboard.Actions;
using Clayboard.Config;
using Clayboard.Core;
using Clayboard.Localization;
using Microsoft.Win32;

namespace Clayboard.UI;

/// <summary>Dialog for creating or editing one binding.</summary>
public partial class BindingEditorWindow : Window
{
    private const int TestDelaySeconds = 3;

    private readonly MainViewModel _viewModel;
    private readonly HotkeyBinding? _original;
    private readonly ObservableCollection<MacroStep> _steps = new();
    private DispatcherTimer? _testCountdown;
    private bool _confirmDelete;
    private bool _loading = true;

    /// <param name="original">The binding to edit, or null to create a new one.</param>
    /// <param name="draft">Starting values for a new binding.</param>
    public BindingEditorWindow(MainViewModel viewModel, HotkeyBinding? original, HotkeyBinding? draft = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _original = original;
        StepList.ItemsSource = _steps;
        _steps.CollectionChanged += (_, _) => RenumberSteps();

        TriggerRecorder.KeysChanged += OnTriggerChanged;
        KeysRecorder.KeysChanged += HideError;
        Deactivated += (_, _) => KeyRecorder.CancelActive();
        Loaded += (_, _) => Motion.ZoomIn(Body);

        HeaderText.SetResourceReference(TextBlock.TextProperty, original is null ? "Ed.New" : "Ed.Edit");
        DeleteButton.Visibility = original is null ? Visibility.Collapsed : Visibility.Visible;
        Load(original ?? draft ?? new HotkeyBinding());
        _loading = false;
        UpdateKeyWarning();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCorners.Round(this);
        // Long macros scroll inside the dialog instead of growing past the screen.
        MaxHeight = SystemParameters.WorkArea.Height - 40;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        KeyRecorder.CancelActive();
        _testCountdown?.Stop();
        base.OnClosing(e);
    }

    private void Load(HotkeyBinding binding)
    {
        TriggerSpec.TryParseMode(binding.Mode, out var mode);
        bool isSequence = TriggerSpec.SplitSteps(binding.Trigger).Length > 1;
        Check(ModeChips, isSequence ? "sequence" : mode switch
        {
            TriggerMode.DoubleTap => "doubleTap",
            TriggerMode.Hold => "hold",
            _ => "press",
        });
        TriggerRecorder.Keys = binding.Trigger;
        HoldBox.Text = binding.HoldMs?.ToString() ?? "";
        NameBox.Text = binding.Name;
        AppBox.Text = binding.App ?? "";
        GroupBox.Text = binding.Group ?? "";
        SuppressSwitch.IsChecked = binding.Suppress;

        var action = binding.Action;
        Check(ActionChips, action.Type);
        PathBox.Text = action.Path ?? "";
        ArgsBox.Text = action.Args ?? "";
        ToggleCloseSwitch.IsChecked = action.Toggle == true;
        ToggleProcessBox.Text = action.Type.Equals("run", StringComparison.OrdinalIgnoreCase) ? action.Process ?? "" : "";
        CloseProcessBox.Text = action.Type.Equals("close", StringComparison.OrdinalIgnoreCase) ? action.Process ?? "" : "";
        ValueBox.Text = ActionRunner.TextOf(action).Replace("\n", Environment.NewLine);
        SecretSwitch.IsChecked = action.Secret is not null;
        KeysRecorder.Keys = action.Keys ?? "";
        if (action.Command is not null)
            Check(MediaChips, action.Command);
        foreach (var step in action.Steps ?? new List<ActionSpec>())
            _steps.Add(MacroStep.From(step));
    }

    /// <summary>Reads the form into a new binding without validating it.</summary>
    private HotkeyBinding Build()
    {
        string mode = CheckedTag(ModeChips) ?? "press";
        string trigger = TriggerRecorder.Keys;
        if (mode != "sequence")
            trigger = TriggerSpec.SplitSteps(trigger).FirstOrDefault() ?? "";

        int? holdMs = null;
        if (mode == "hold" && HoldBox.Text.Trim().Length > 0)
            holdMs = int.TryParse(HoldBox.Text.Trim(), out int ms) ? ms : -1; // -1 fails validation.

        return new HotkeyBinding
        {
            Name = NameBox.Text.Trim(),
            Trigger = trigger,
            Mode = mode is "doubleTap" or "hold" ? mode : null,
            HoldMs = holdMs,
            App = string.IsNullOrWhiteSpace(AppBox.Text) ? null : AppBox.Text.Trim(),
            Group = NullIfBlank(GroupBox.Text), // A new name becomes a new group when saved.
            Enabled = _original?.Enabled ?? true,
            Suppress = SuppressSwitch.IsChecked == true,
            Action = BuildAction(),
        };
    }

    private ActionSpec BuildAction()
    {
        string type = CheckedTag(ActionChips) ?? "run";
        return type switch
        {
            "run" => new ActionSpec
            {
                Type = type,
                Path = PathBox.Text.Trim(),
                Args = NullIfBlank(ArgsBox.Text),
                Toggle = ToggleCloseSwitch.IsChecked == true ? true : null,
                Process = ToggleCloseSwitch.IsChecked == true ? NullIfBlank(ToggleProcessBox.Text) : null,
            },
            "close" => new ActionSpec { Type = type, Process = NullIfBlank(CloseProcessBox.Text) },
            "text" when SecretSwitch.IsChecked == true =>
                new ActionSpec { Type = type, Secret = Secrets.Protect(ValueBox.Text.Replace("\r\n", "\n")) },
            "text" => new ActionSpec { Type = type, Value = ValueBox.Text.Replace("\r\n", "\n") },
            "sendKeys" => new ActionSpec { Type = type, Keys = KeysRecorder.Keys },
            "macro" => new ActionSpec { Type = type, Steps = _steps.Select(s => s.ToSpec()).ToList() },
            _ => new ActionSpec { Type = type, Command = CheckedTag(MediaChips) },
        };
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private string? Validate(HotkeyBinding binding)
    {
        if (TriggerRecorder.Keys.Length == 0)
            return Loc.T("Ed.Err.NoKeys");
        if (CheckedTag(ModeChips) == "sequence" && TriggerSpec.SplitSteps(binding.Trigger).Length < 2)
            return Loc.T("Ed.Err.NoNext");
        if (!TriggerSpec.TryParse(binding, out var trigger, out var triggerError))
            return triggerError;
        if (ActionRunner.Validate(binding.Action) is { } actionError)
            return actionError;
        if (binding.Enabled && _viewModel.FindConflict(trigger!, binding.App, _original) is { } other)
            return Loc.F("Ed.Err.Conflict", other);
        return null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var binding = Build();
        if (Validate(binding) is { } error)
        {
            ShowError(error);
            return;
        }
        if (!_viewModel.SaveBinding(_original, binding, out var saveError))
        {
            ShowError(saveError!);
            return;
        }
        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_original is null)
            return;
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            DeleteButton.SetResourceReference(ContentProperty, "Ed.DeleteConfirm");
            Motion.FadeIn(DeleteButton, fromY: 0, ms: 160);
            return;
        }
        if (!_viewModel.DeleteBinding(_original, out var error))
        {
            ShowError(error!);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (_testCountdown is not null)
            return;

        var action = BuildAction();
        if (ActionRunner.Validate(action) is { } error)
        {
            ShowError(error);
            return;
        }
        HideError();

        // Typed text and keys go to whichever window has focus, so give time to switch to it.
        if (ActionRunner.TypesIntoFocusedWindow(action))
            StartTestCountdown(action);
        else
            RunTest(action);
    }

    private void StartTestCountdown(ActionSpec action)
    {
        int remaining = TestDelaySeconds;
        TestLabel.Text = Loc.F("Ed.Countdown", remaining);
        _testCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _testCountdown.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                TestLabel.Text = Loc.F("Ed.Countdown", remaining);
                return;
            }
            _testCountdown.Stop();
            _testCountdown = null;
            TestLabel.SetResourceReference(TextBlock.TextProperty, "Ed.Test");
            RunTest(action);
        };
        _testCountdown.Start();
    }

    private void RunTest(ActionSpec action)
    {
        Task.Run(() =>
        {
            try
            {
                ActionRunner.Run(action);
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() => ShowError(Loc.F("Ed.TestFailed", ex.Message)));
            }
        });
    }

    private void OnTriggerChanged()
    {
        HideError();
        UpdateKeyWarning();
        var binding = Build();
        if (TriggerSpec.TryParse(binding, out var trigger, out _)
            && _viewModel.FindConflict(trigger!, binding.App, _original) is { } other)
        {
            ShowError(Loc.F("Ed.Err.Conflict", other));
        }
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (ModeHint is null)
            return; // Fires during InitializeComponent before the rest of the form exists.

        var mode = (string)((RadioButton)sender).Tag;
        MoveModeThumb(animate: !_loading);

        // Leaving Sequence keeps only the first step, so the box never shows keys that won't be saved.
        TriggerRecorder.Multi = mode == "sequence";
        if (mode != "sequence" && TriggerSpec.SplitSteps(TriggerRecorder.Keys).Length > 1)
            TriggerRecorder.Keys = TriggerSpec.SplitSteps(TriggerRecorder.Keys)[0];

        SetShown(HoldPanel, mode == "hold");
        UpdateKeyWarning();
        if (mode is "doubleTap" or "hold" or "sequence")
        {
            ModeHint.SetResourceReference(TextBlock.TextProperty, mode switch
            {
                "doubleTap" => "Ed.Hint.DoubleTap",
                "hold" => "Ed.Hint.Hold",
                _ => "Ed.Hint.Sequence",
            });
            ModeHint.Visibility = Visibility.Visible;
            if (!_loading)
                Motion.FadeIn(ModeHint, fromY: 4, ms: 180);
        }
        else
        {
            ModeHint.Visibility = Visibility.Collapsed;
        }
        HideError();
    }

    private void OnSuppressChanged(object sender, RoutedEventArgs e) => UpdateKeyWarning();

    /// <summary>
    /// Warns when a blocked binding would take over an everyday key (a letter, Enter, Space…)
    /// in every app. It's allowed, but easy to do by accident.
    /// </summary>
    private void UpdateKeyWarning()
    {
        if (_loading || KeyWarning is null)
            return;
        bool warn = CheckedTag(ModeChips) == "press" && SuppressSwitch.IsChecked == true
            && KeyCombo.TryParse(TriggerRecorder.Keys, out var combo, out _)
            && (combo.Mods & (Mods.Ctrl | Mods.Alt | Mods.Win)) == 0
            && !IsSpareKey(combo.Vk);
        SetShown(KeyWarning, warn);
    }

    // Keys most apps leave alone: F1–F24, browser and media keys, Pause, Scroll Lock, Print Screen.
    private static bool IsSpareKey(int vk) =>
        vk is (>= 0x70 and <= 0x87) or (>= 0xA6 and <= 0xB7) or 0x13 or 0x91 or 0x2C;

    private void OnModeBarSizeChanged(object sender, SizeChangedEventArgs e) => MoveModeThumb(animate: false);

    /// <summary>Sizes the selection pill to one option and glides it under the checked one.</summary>
    private void MoveModeThumb(bool animate)
    {
        var options = ModeChips.Children.OfType<RadioButton>().ToList();
        int index = options.FindIndex(r => r.IsChecked == true);
        if (index < 0 || ModeChips.ActualWidth <= 0)
            return;
        double width = ModeChips.ActualWidth / options.Count;
        ModeThumb.Width = width;
        ModeThumb.Height = ModeChips.ActualHeight;
        if (animate)
        {
            Motion.SlideX(ModeThumbShift, index * width);
        }
        else
        {
            ModeThumbShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            ModeThumbShift.X = index * width;
        }
    }

    private void OnActionTypeChecked(object sender, RoutedEventArgs e)
    {
        if (RunPanel is null)
            return; // Fires during InitializeComponent before the panels exist.

        var type = (string)((RadioButton)sender).Tag;
        SetShown(RunPanel, type == "run");
        SetShown(ClosePanel, type == "close");
        SetShown(TextPanel, type == "text");
        SetShown(KeysPanel, type == "sendKeys");
        SetShown(MacroPanel, type == "macro");
        SetShown(MediaPanel, type == "media");
        if (type != "sendKeys")
            KeysRecorder.Cancel();
        HideError();
    }

    private void OnToggleCloseChanged(object sender, RoutedEventArgs e) =>
        SetShown(ToggleTargetPanel, ToggleCloseSwitch.IsChecked == true);

    // The "which app to close" box suggests the program itself, which is right most of the time.
    private void OnPathChanged(object sender, TextChangedEventArgs e)
    {
        var target = ActionRunner.ToggleTarget(new ActionSpec { Path = PathBox.Text });
        ToggleProcessBox.Tag = target is null ? "notepad.exe" : System.IO.Path.GetFileName(target.Trim('"'));
    }

    /// <summary>Shows or hides a part of the form, easing it in when it appears.</summary>
    private void SetShown(UIElement element, bool shown)
    {
        bool wasShown = element.Visibility == Visibility.Visible;
        element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown && !wasShown && !_loading)
            Motion.FadeIn(element, fromY: 6, ms: 200);
    }

    private void OnAddStep(object sender, RoutedEventArgs e)
    {
        var menu = AddStepButton.ContextMenu!;
        menu.PlacementTarget = AddStepButton;
        menu.IsOpen = true;
    }

    private void OnAddStepOfType(object sender, RoutedEventArgs e) =>
        _steps.Add(new MacroStep((string)((MenuItem)sender).Tag));

    private void OnStepLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            Motion.FadeIn((UIElement)sender, fromY: 6, ms: 180);
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e) => _steps.Remove(StepOf(sender));

    private void OnMoveStepUp(object sender, RoutedEventArgs e) => MoveStep(StepOf(sender), -1);

    private void OnMoveStepDown(object sender, RoutedEventArgs e) => MoveStep(StepOf(sender), +1);

    private void MoveStep(MacroStep step, int offset)
    {
        int from = _steps.IndexOf(step);
        int to = from + offset;
        if (to >= 0 && to < _steps.Count)
            _steps.Move(from, to);
    }

    private void RenumberSteps()
    {
        for (int i = 0; i < _steps.Count; i++)
            _steps[i].Number = i + 1;
    }

    private static MacroStep StepOf(object sender) => (MacroStep)((FrameworkElement)sender).DataContext;

    /// <summary>Offers running apps for the text box named in the button's Tag.</summary>
    private void OnChooseApp(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)((Button)sender).Tag;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        if (box == AppBox)
        {
            var everyApp = new MenuItem { Header = Loc.T("Ed.EveryApp") };
            everyApp.Click += (_, _) => box.Text = "";
            menu.Items.Add(everyApp);
        }
        foreach (var name in RunningAppNames())
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => box.Text = name;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnChooseGroup(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var none = new MenuItem { Header = Loc.T("Group.None") };
        none.Click += (_, _) => GroupBox.Text = "";
        menu.Items.Add(none);
        foreach (var group in _viewModel.Groups)
        {
            var item = new MenuItem { Header = group };
            item.Click += (_, _) => GroupBox.Text = group;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Exe names of apps that currently show a window, such as "chrome.exe".</summary>
    private static IEnumerable<string> RunningAppNames()
    {
        int self = Environment.ProcessId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != self && process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle.Length > 0)
                        names.Add(process.ProcessName + ".exe");
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // The process exited or belongs to another user.
                }
            }
        }
        return names;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Ed.BrowseTitle"),
            Filter = $"{Loc.T("Ed.Programs")} (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|{Loc.T("Ed.AllFiles")} (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FileName;
    }

    private void OnDragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void ShowError(string message)
    {
        bool wasShown = ErrorText.Visibility == Visibility.Visible && ErrorText.Text == message;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        if (!wasShown)
            Motion.FadeIn(ErrorText, fromY: 4, ms: 180);
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    private static string? CheckedTag(Panel chips) =>
        chips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private static void Check(Panel chips, string? tag)
    {
        var chip = chips.Children.OfType<RadioButton>()
            .FirstOrDefault(r => string.Equals((string)r.Tag, tag?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chip is not null)
            chip.IsChecked = true;
    }
}
