using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clayboard.Config;
using Clayboard.Core;
using Clayboard.Localization;
using Microsoft.Win32;

namespace Clayboard.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _syncingStartupSwitch;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        StateChanged += (_, _) => UpdateMaximizedLayout();
        Loc.Changed += UpdateMaximizedLayout;
        BuildLanguageChips();
        BuildThemeButton();
        Loc.Changed += BuildThemeButton;
        Palette.Changed += BuildThemeButton;
        UpdateMaximizedLayout();
        _drag = new RowDragController(BindingList, ListScroller, viewModel);
    }

    private void BuildLanguageChips()
    {
        foreach (var (code, nativeName) in Loc.Languages)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "Language",
                Content = nativeName, // Each language in its own script, so anyone can find theirs.
                Tag = code,
                IsChecked = code == _viewModel.Language,
            };
            chip.Checked += (_, _) => _viewModel.SetLanguage(code);
            LanguageChips.Children.Add(chip);
        }
    }

    // ---- Theme dropdown. Picking a theme repaints every window in place (see Palette.Apply); the button's
    // swatch and name are filled in again whenever the theme or the language changes. ----

    private static IEnumerable<(string Key, ThemeColors Colors)> ThemeChoices() =>
        new[] { (Palette.SystemKey, Palette.FromWindows()) }.Concat(Palette.Themes.Select(t => (t.Key, t)));

    // Original and Follow Windows are translated; the other theme names are the same in every language.
    private static string ThemeLabel(string key, ThemeColors colors) => key switch
    {
        Palette.SystemKey => Loc.T("Theme.System"),
        Palette.OriginalKey => Loc.T("Theme.Original"),
        _ => colors.Name,
    };

    private void BuildThemeButton()
    {
        var key = _viewModel.Theme;
        var label = ThemeLabel(key, Palette.Resolve(key));
        ThemeButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                ThemeSwatch(Palette.Resolve(key)),
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Style = (Style)FindResource("Icon"), Text = "", FontSize = 9, Margin = new Thickness(10, 1, 0, 0),
                },
            },
        };
        AutomationProperties.SetName(ThemeButton, $"{Loc.T("Settings.Theme")}: {label}");
    }

    private void OnThemeMenu(object sender, RoutedEventArgs e)
    {
        var current = _viewModel.Theme;
        var menu = new ContextMenu { PlacementTarget = ThemeButton, MinWidth = ThemeButton.ActualWidth };
        foreach (var (key, colors) in ThemeChoices())
        {
            var label = ThemeLabel(key, colors);
            var check = new TextBlock
            {
                Style = (Style)FindResource("Icon"),
                Text = key == current ? "" : "",
                FontSize = 10,
                Width = 20,
            };
            var item = new MenuItem
            {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { check, ThemeSwatch(colors), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            // The header is a check, a swatch, and text, so name the item for screen readers.
            AutomationProperties.SetName(item, label);
            item.Click += (_, _) => _viewModel.SetTheme(key);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>A small preview of a theme: background, card, and accent side by side.</summary>
    private static Border ThemeSwatch(ThemeColors colors) => new()
    {
        Width = 30,
        Height = 14,
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        BorderBrush = Palette.Frozen(colors.Line),
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new UniformGrid
        {
            Rows = 1,
            Children =
            {
                new Border { Background = Palette.Frozen(colors.Bg), CornerRadius = new CornerRadius(3, 0, 0, 3) },
                new Border { Background = Palette.Frozen(colors.Card) },
                new Border { Background = Palette.Frozen(colors.Clay), CornerRadius = new CornerRadius(0, 3, 3, 0) },
            },
        },
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCorners.Round(this);
    }

    // The window really closes when sent to the tray (the app rebuilds it on demand), so let go of
    // app-wide events or the closed window would stay in memory.
    protected override void OnClosed(EventArgs e)
    {
        Loc.Changed -= UpdateMaximizedLayout;
        Loc.Changed -= BuildThemeButton;
        Palette.Changed -= BuildThemeButton;
        _undoTimer?.Stop();
        base.OnClosed(e);
    }

    private void UpdateMaximizedLayout()
    {
        // A maximized borderless window overhangs the screen edge by the resize border.
        bool maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
        Root.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = Loc.T(maximized ? "Win.Restore" : "Win.Maximize");
        AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnTogglePause(object sender, RoutedEventArgs e) => _viewModel.Paused = !_viewModel.Paused;

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Reload();

    private void OnNew(object sender, RoutedEventArgs e) =>
        new BindingEditorWindow(_viewModel, null, _viewModel.NewDraft()) { Owner = this }.ShowDialog();

    // The row's switch handles its own clicks, so this only fires for the rest of the row.
    private void OnRowClick(object sender, MouseButtonEventArgs e) => OpenEditor((BindingItem)((FrameworkElement)sender).DataContext);

    private void OpenEditor(BindingItem item) =>
        new BindingEditorWindow(_viewModel, item.Model) { Owner = this }.ShowDialog();

    // Enter or Space on a focused row, or a screen reader's invoke.
    private void OnRowInvoked(object sender, RoutedEventArgs e) => OpenEditor((BindingItem)((FrameworkElement)sender).DataContext);

    // Ctrl+Up/Down moves the focused row, like its arrow buttons.
    private void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || Keyboard.Modifiers != ModifierKeys.Control
            || e.Key is not (Key.Up or Key.Down) || !_viewModel.CanReorder)
            return;
        var item = (BindingItem)((FrameworkElement)sender).DataContext;
        if (e.Key == Key.Up)
            _viewModel.MoveUp(item);
        else
            _viewModel.MoveDown(item);
        FocusRowOf(item); // The row's control may have moved; keep focus on it for the next press.
        e.Handled = true;
    }

    private void FocusRowOf(object item) => Dispatcher.InvokeAsync(() =>
    {
        if (BindingList.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter container
            && VisualTreeHelper.GetChildrenCount(container) > 0
            && VisualTreeHelper.GetChild(container, 0) is UIElement row)
            row.Focus();
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnHeaderInvoked(object sender, RoutedEventArgs e) =>
        _viewModel.ToggleCollapsed((GroupHeader)((FrameworkElement)sender).DataContext);

    private void OnProfileInvoked(object sender, RoutedEventArgs e) => OpenProfile(sender);

    // ---- Bindings list: groups, sorting, and moving rows ----

    private readonly RowDragController _drag;
    private object? _dragCandidate;
    private Point _dragOrigin;

    private void OnHeaderClick(object sender, MouseButtonEventArgs e) =>
        _viewModel.ToggleCollapsed((GroupHeader)((FrameworkElement)sender).DataContext);

    private void OnMoveUp(object sender, RoutedEventArgs e) =>
        _viewModel.MoveUp((BindingItem)((FrameworkElement)sender).DataContext);

    private void OnMoveDown(object sender, RoutedEventArgs e) =>
        _viewModel.MoveDown((BindingItem)((FrameworkElement)sender).DataContext);

    private void OnNewGroup(object sender, RoutedEventArgs e) =>
        new TextPromptWindow(Loc.T("Group.New"), Loc.T("Group.NamePlaceholder"), "", _viewModel.AddGroup)
        {
            Owner = this,
        }.ShowDialog();

    private void OnGroupMenu(object sender, RoutedEventArgs e)
    {
        var header = (GroupHeader)((FrameworkElement)sender).DataContext;
        var rename = new MenuItem { Header = Loc.T("Group.Rename") };
        rename.Click += (_, _) => new TextPromptWindow(Loc.T("Group.Rename"), Loc.T("Group.NamePlaceholder"),
            header.Name!, name => _viewModel.RenameGroup(header.Name!, name)) { Owner = this }.ShowDialog();
        var delete = new MenuItem { Header = Loc.T("Group.Delete") };
        delete.Click += (_, _) => ShowUndo(_viewModel.DeleteGroup(header.Name!));
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Items = { rename, delete } };
        menu.IsOpen = true;
    }

    private MainViewModel.DeletedGroup? _deletedGroup;
    private DispatcherTimer? _undoTimer;

    private void ShowUndo(MainViewModel.DeletedGroup deleted)
    {
        _deletedGroup = deleted;
        UndoText.Text = Loc.F("Group.Deleted", deleted.Name);
        UndoBanner.Visibility = Visibility.Visible;
        Motion.FadeIn(UndoBanner, fromY: 4);
        if (_undoTimer is null)
        {
            _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _undoTimer.Tick += (_, _) => HideUndo();
        }
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void HideUndo()
    {
        _undoTimer?.Stop();
        _deletedGroup = null;
        UndoBanner.Visibility = Visibility.Collapsed;
    }

    private void OnUndoDeleteGroup(object sender, RoutedEventArgs e)
    {
        if (_deletedGroup is { } deleted)
            _viewModel.RestoreGroup(deleted);
        HideUndo();
    }

    private void OnSortMenu(object sender, RoutedEventArgs e) =>
        ShowChoiceMenu((UIElement)sender, MainViewModel.SortModes, _viewModel.SortMode, MainViewModel.SortName,
            _viewModel.SetSort);

    private void OnShowMenu(object sender, RoutedEventArgs e) =>
        ShowChoiceMenu((UIElement)sender, MainViewModel.ShowModes, _viewModel.ShowMode, MainViewModel.ShowName,
            _viewModel.SetShow);

    /// <summary>A dropdown of choices with a check next to the current one.</summary>
    private static void ShowChoiceMenu(UIElement anchor, IReadOnlyList<string> modes, string current,
        Func<string, string> label, Action<string> choose)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var mode in modes)
        {
            var check = new TextBlock
            {
                Style = (Style)Application.Current.FindResource("Icon"),
                Text = mode == current ? "" : "",
                FontSize = 10,
                Width = 20,
            };
            var item = new MenuItem
            {
                Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { check, new TextBlock { Text = label(mode) } } },
            };
            // The header is a check icon plus text, so name the item for screen readers.
            AutomationProperties.SetName(item, label(mode));
            item.Click += (_, _) => choose(mode);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // Dragging starts from the handle only, so clicking a row still opens it for editing.
    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.CanReorder || _drag.IsDragging)
            return;
        _dragCandidate = ((FrameworkElement)sender).DataContext;
        _dragOrigin = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true; // A press on the handle isn't a click on the row.
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var moved = e.GetPosition(this) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _dragCandidate;
        _dragCandidate = null;
        ((UIElement)sender).ReleaseMouseCapture();
        _drag.Begin(item); // From here the list holds the mouse until the button is released.
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag.IsDragging)
            _drag.Move();
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drag.IsDragging)
            return;
        _drag.Drop();
        e.Handled = true;
    }

    // Losing the mouse mid-drag (another window took focus, say) puts everything back.
    private void OnListLostCapture(object sender, MouseEventArgs e)
    {
        if (_drag.IsDragging)
            _drag.Cancel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drag.IsDragging)
        {
            _drag.Cancel();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
    private void OnProfileClick(object sender, MouseButtonEventArgs e) => OpenProfile(sender);

    private void OpenProfile(object sender)
    {
        _viewModel.ShowProfile((ProfileSummary)((FrameworkElement)sender).DataContext);
        ShowPage(BindingsPage);
    }

    private void OnShowAll(object sender, RoutedEventArgs e) => NavBindings.IsChecked = true;

    private void OnAllowUntrusted(object sender, RoutedEventArgs e) => _viewModel.AllowUntrusted();

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingStartupSwitch)
            return;
        try
        {
            StartupManager.SetEnabled(StartupSwitch.IsChecked == true);
            SettingsError.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            SettingsError.Text = Loc.F("Settings.StartupError", ex.Message);
            SettingsError.Visibility = Visibility.Visible;
        }
        SyncStartupSwitch();
    }

    /// <summary>Shows the real registry state without treating it as a user toggle.</summary>
    private void SyncStartupSwitch()
    {
        _syncingStartupSwitch = true;
        StartupSwitch.IsChecked = StartupManager.IsEnabled;
        _syncingStartupSwitch = false;
    }

    private List<HotkeyBinding>? _pendingImport;
    private List<string> _pendingGroups = new();
    private ExportedSettings? _pendingSettings;

    private string FileFilter => $"{Loc.T("Settings.FileFilter")} (*.json)|*.json";

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Settings.Export"),
            Filter = FileFilter,
            DefaultExt = ".json",
            FileName = $"Clayboard-backup-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            int count = _viewModel.ExportBindings(dialog.FileName);
            ShowBackupStatus(Loc.F("Settings.ExportDone", count, Path.GetFileName(dialog.FileName)), isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBackupStatus(Loc.F("Settings.ExportError", ex.Message), isError: true);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Settings.Import"),
            Filter = $"{FileFilter}|{Loc.T("Ed.AllFiles")} (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var bindings = ConfigStore.ReadBindingsFile(dialog.FileName, out _pendingGroups, out _pendingSettings);
            if (bindings.Count == 0)
            {
                if (_pendingSettings is null)
                {
                    ShowBackupStatus(Loc.T("Settings.ImportNone"), isError: true);
                    return;
                }
                // Settings alone: there are no bindings to merge, so nothing to ask.
                ApplyPendingSettings();
                ShowBackupStatus(Loc.T("Settings.ImportSettingsDone"), isError: false);
                return;
            }
            // Ask how to merge before changing anything.
            _pendingImport = bindings;
            ImportFoundText.Text = Loc.F("Settings.ImportFound", bindings.Count, Path.GetFileName(dialog.FileName))
                + (_pendingSettings is null ? "" : " " + Loc.T("Settings.ImportHasSettings"));
            BackupStatus.Visibility = Visibility.Collapsed;
            ImportChoice.Visibility = Visibility.Visible;
            Motion.FadeIn(ImportChoice, fromY: 4);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowBackupStatus(Loc.F("Settings.ImportError", ex.Message), isError: true);
        }
    }

    private void OnImportAdd(object sender, RoutedEventArgs e) => FinishImport(replace: false);

    private void OnImportReplace(object sender, RoutedEventArgs e) => FinishImport(replace: true);

    private void OnImportCancel(object sender, RoutedEventArgs e)
    {
        _pendingImport = null;
        _pendingSettings = null;
        ImportChoice.Visibility = Visibility.Collapsed;
    }

    private void FinishImport(bool replace)
    {
        if (_pendingImport is null)
            return;
        var (added, backupPath) = _viewModel.ImportBindings(_pendingImport, _pendingGroups, replace);
        _pendingImport = null;
        ImportChoice.Visibility = Visibility.Collapsed;
        bool hadSettings = ApplyPendingSettings(); // After the bindings, so folded groups match the imported ones.
        ShowBackupStatus((replace
            ? Loc.F("Settings.ImportReplaced", added, backupPath)
            : Loc.F("Settings.ImportDone", added))
            + (hadSettings ? " " + Loc.T("Settings.ImportSettingsDone") : ""), isError: false);
    }

    /// <summary>Takes on the settings read from an import file, if it had any. Returns whether it did.</summary>
    private bool ApplyPendingSettings()
    {
        var settings = _pendingSettings;
        _pendingSettings = null;
        if (settings is null)
            return false;
        _viewModel.ImportSettings(settings);
        SettingsError.Visibility = Visibility.Collapsed;
        if (settings.StartWithWindows is { } startup && startup != StartupManager.IsEnabled)
        {
            try
            {
                StartupManager.SetEnabled(startup);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                SettingsError.Text = Loc.F("Settings.StartupError", ex.Message);
                SettingsError.Visibility = Visibility.Visible;
            }
        }
        SyncSettingsPage();
        return true;
    }

    private void ShowBackupStatus(string message, bool isError)
    {
        BackupStatus.Text = message;
        BackupStatus.SetResourceReference(TextBlock.ForegroundProperty, isError ? "TerraBrush" : "MuteBrush");
        BackupStatus.Visibility = Visibility.Visible;
        Motion.FadeIn(BackupStatus, fromY: 4);
    }

    private void OnSequenceWaitKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnSequenceWaitCommitted(sender, e);
    }

    private void OnSequenceWaitCommitted(object sender, RoutedEventArgs e)
    {
        // Anything unreadable falls back to the current value; out-of-range numbers are clamped.
        int ms = int.TryParse(SequenceWaitBox.Text.Trim(), out int typed) ? typed : _viewModel.SequenceTimeoutMs;
        SequenceWaitBox.Text = _viewModel.SetSequenceTimeout(ms).ToString();
    }

    // Full paths to Windows' own tools: a bare name is looked up next to our exe first, where a portable
    // copy in Downloads could find a planted look-alike.
    private static string WindowsTool(string exe) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) =>
        Process.Start(WindowsTool("explorer.exe"), $"/select,\"{_viewModel.ConfigPath}\"");

    private void OnEditConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_viewModel.ConfigPath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No app is registered for .json files.
            Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), $"\"{_viewModel.ConfigPath}\"");
        }
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (BindingsPage is null)
            return; // Fires during InitializeComponent before the pages exist.

        switch ((string)((RadioButton)sender).Tag)
        {
            case "Bindings":
                _viewModel.ShowAll();
                ShowPage(BindingsPage);
                break;
            case "Macros":
                _viewModel.ShowMacros();
                ShowPage(BindingsPage);
                break;
            case "Profiles":
                ShowPage(ProfilesPage);
                break;
            default:
                SyncSettingsPage();
                ShowPage(SettingsPage);
                break;
        }
    }

    /// <summary>Shows the current settings in the Settings page's controls.</summary>
    private void SyncSettingsPage()
    {
        SyncStartupSwitch();
        SequenceWaitBox.Text = _viewModel.SequenceTimeoutMs.ToString();
        foreach (RadioButton chip in LanguageChips.Children)
            chip.IsChecked = (string)chip.Tag == Loc.Language;
    }

    private void ShowPage(FrameworkElement page)
    {
        foreach (var candidate in new FrameworkElement[] { BindingsPage, ProfilesPage, SettingsPage })
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
        Motion.FadeIn(page, fromY: 10, ms: 220);
    }

    // Rows rise into place as they appear, so a switched filter or a new binding doesn't just blink in.
    private void OnRowLoaded(object sender, RoutedEventArgs e) => Motion.FadeIn((UIElement)sender, fromY: 4, ms: 160);
}
