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
using MacroInWZa007.Actions;
using MacroInWZa007.Core;
using Microsoft.Win32;

namespace MacroInWZa007.UI;

/// <summary>Dialog for creating or editing one binding.</summary>
public partial class BindingEditorWindow : Window
{
    private const int TestDelaySeconds = 3;

    private readonly MainViewModel _viewModel;
    private readonly HotkeyBinding? _original;
    private readonly ObservableCollection<SequenceStep> _sequence = new();
    private readonly ObservableCollection<MacroStep> _steps = new();
    private DispatcherTimer? _testCountdown;
    private bool _confirmDelete;

    /// <param name="original">The binding to edit, or null to create a new one.</param>
    /// <param name="draft">Starting values for a new binding.</param>
    public BindingEditorWindow(MainViewModel viewModel, HotkeyBinding? original, HotkeyBinding? draft = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _original = original;
        SequenceList.ItemsSource = _sequence;
        StepList.ItemsSource = _steps;
        _steps.CollectionChanged += (_, _) => RenumberSteps();

        TriggerRecorder.KeysChanged += OnTriggerChanged;
        KeysRecorder.KeysChanged += HideError;
        Deactivated += (_, _) => KeyRecorder.CancelActive();

        HeaderText.Text = original is null ? "New binding" : "Edit binding";
        DeleteButton.Visibility = original is null ? Visibility.Collapsed : Visibility.Visible;
        Load(original ?? draft ?? new HotkeyBinding());
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
        var steps = TriggerSpec.SplitSteps(binding.Trigger);
        TriggerRecorder.Keys = steps.FirstOrDefault() ?? "";
        foreach (var step in steps.Skip(1))
            _sequence.Add(new SequenceStep { Keys = step });

        TriggerSpec.TryParseMode(binding.Mode, out var mode);
        Check(ModeChips, steps.Length > 1 ? "sequence" : mode switch
        {
            TriggerMode.DoubleTap => "doubleTap",
            TriggerMode.Hold => "hold",
            _ => "press",
        });
        HoldBox.Text = binding.HoldMs?.ToString() ?? "";
        NameBox.Text = binding.Name;
        AppBox.Text = binding.App ?? "";
        SuppressSwitch.IsChecked = binding.Suppress;

        var action = binding.Action;
        Check(ActionChips, action.Type);
        PathBox.Text = action.Path ?? "";
        ArgsBox.Text = action.Args ?? "";
        ValueBox.Text = (action.Value ?? "").Replace("\n", Environment.NewLine);
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
        if (mode == "sequence")
        {
            var keys = new[] { trigger }.Concat(_sequence.Select(s => s.Keys)).Where(k => k.Length > 0);
            trigger = string.Join(", ", keys);
        }

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
                Args = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim(),
            },
            "text" => new ActionSpec { Type = type, Value = ValueBox.Text.Replace("\r\n", "\n") },
            "sendKeys" => new ActionSpec { Type = type, Keys = KeysRecorder.Keys },
            "macro" => new ActionSpec { Type = type, Steps = _steps.Select(s => s.ToSpec()).ToList() },
            _ => new ActionSpec { Type = type, Command = CheckedTag(MediaChips) },
        };
    }

    private string? Validate(HotkeyBinding binding)
    {
        if (TriggerRecorder.Keys.Length == 0)
            return "Record the keys that start this binding.";
        if (CheckedTag(ModeChips) == "sequence" && !_sequence.Any(s => s.Keys.Length > 0))
            return "Add the keys to press next, or pick Press instead of Sequence.";
        if (!TriggerSpec.TryParse(binding, out var trigger, out var triggerError))
            return triggerError;
        if (ActionRunner.Validate(binding.Action) is { } actionError)
            return actionError;
        if (binding.Enabled && _viewModel.FindConflict(trigger!, binding.App, _original) is { } other)
            return $"These keys are already used by \"{other}\".";
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
            DeleteButton.Content = "Click again to delete";
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
        TestLabel.Text = $"Switch window… {remaining}";
        _testCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _testCountdown.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                TestLabel.Text = $"Switch window… {remaining}";
                return;
            }
            _testCountdown.Stop();
            _testCountdown = null;
            TestLabel.Text = "Test";
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
                Dispatcher.InvokeAsync(() => ShowError($"Test failed. {ex.Message}"));
            }
        });
    }

    private void OnTriggerChanged()
    {
        HideError();
        if (TriggerSpec.TryParse(TriggerRecorder.Keys, Build().Mode, null, out var trigger, out _)
            && CheckedTag(ModeChips) != "sequence"
            && _viewModel.FindConflict(trigger!, AppBox.Text, _original) is { } other)
        {
            ShowError($"These keys are already used by \"{other}\".");
        }
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (SequencePanel is null)
            return; // Fires during InitializeComponent before the panels exist.

        var mode = (string)((RadioButton)sender).Tag;
        SequencePanel.Visibility = mode == "sequence" ? Visibility.Visible : Visibility.Collapsed;
        HoldPanel.Visibility = mode == "hold" ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "sequence" && _sequence.Count == 0)
            _sequence.Add(new SequenceStep());
        ModeHint.Text = mode switch
        {
            "doubleTap" => "Press the keys twice quickly. Works with a lone Shift, Ctrl, or Alt too.",
            "hold" => "Keep the keys down. A quick tap still reaches the app as usual.",
            "sequence" => "Press each step within 1.5 seconds of the last, like Ctrl+K then Ctrl+B.",
            _ => "",
        };
        ModeHint.Visibility = ModeHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        HideError();
    }

    private void OnAddSequenceStep(object sender, RoutedEventArgs e) => _sequence.Add(new SequenceStep());

    private void OnRemoveSequenceStep(object sender, RoutedEventArgs e) =>
        _sequence.Remove((SequenceStep)((FrameworkElement)sender).DataContext);

    private void OnActionTypeChecked(object sender, RoutedEventArgs e)
    {
        if (RunPanel is null)
            return; // Fires during InitializeComponent before the panels exist.

        var type = (string)((RadioButton)sender).Tag;
        RunPanel.Visibility = type == "run" ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = type == "text" ? Visibility.Visible : Visibility.Collapsed;
        KeysPanel.Visibility = type == "sendKeys" ? Visibility.Visible : Visibility.Collapsed;
        MacroPanel.Visibility = type == "macro" ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = type == "media" ? Visibility.Visible : Visibility.Collapsed;
        if (type != "sendKeys")
            KeysRecorder.Cancel();
        HideError();
    }

    private void OnAddStep(object sender, RoutedEventArgs e)
    {
        var menu = AddStepButton.ContextMenu!;
        menu.PlacementTarget = AddStepButton;
        menu.IsOpen = true;
    }

    private void OnAddStepOfType(object sender, RoutedEventArgs e) =>
        _steps.Add(new MacroStep((string)((MenuItem)sender).Tag));

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

    private void OnChooseApp(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var everyApp = new MenuItem { Header = "Every app" };
        everyApp.Click += (_, _) => AppBox.Text = "";
        menu.Items.Add(everyApp);
        foreach (var name in RunningAppNames())
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => AppBox.Text = name;
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
            Title = "Choose a program or file",
            Filter = "Programs (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|All files (*.*)|*.*",
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
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
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
