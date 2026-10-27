using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
    private void OnRowClick(object sender, MouseButtonEventArgs e) =>
        new BindingEditorWindow(_viewModel, ((BindingItem)((FrameworkElement)sender).DataContext).Model)
        {
            Owner = this,
        }.ShowDialog();

    // ---- Bindings list: groups, sorting, and moving rows ----

    private const string DragFormat = "Clayboard.Row";
    private object? _dragCandidate;
    private Point _dragOrigin;
    private InsertionAdorner? _insertion;

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
        delete.Click += (_, _) => _viewModel.DeleteGroup(header.Name!);
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Items = { rename, delete } };
        menu.IsOpen = true;
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
        if (!_viewModel.CanReorder)
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
        if (BindingList.ItemContainerGenerator.ContainerFromItem(item) is not UIElement container)
            return;
        container.Opacity = 0.35; // The row being moved stays visible as a ghost in its old place.
        try
        {
            DragDrop.DoDragDrop(container, new DataObject(DragFormat, item), DragDropEffects.Move);
        }
        finally
        {
            container.Opacity = 1;
            HideInsertion();
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        ScrollNearEdges(e);
        if (FindDropTarget(e) is { } target)
            ShowInsertion(target.LineY);
        else
            HideInsertion();
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideInsertion();
        if (e.Data.GetData(DragFormat) is { } dragged && FindDropTarget(e) is { } target)
            _viewModel.MoveRow(dragged, target.Item, target.After);
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => HideInsertion();

    /// <summary>
    /// The row under the pointer and whether the drop goes after it. Groups can only be dropped
    /// between groups, so for them the line sits above a header or below that group's last row.
    /// </summary>
    private (object Item, bool After, double LineY)? FindDropTarget(DragEventArgs e)
    {
        var dragged = e.Data.GetData(DragFormat);
        double pointer = e.GetPosition(BindingList).Y;
        var rows = _viewModel.Rows;
        (object Item, double Top, double Bottom)? last = null;

        for (int i = 0; i < rows.Count; i++)
        {
            if (dragged is GroupHeader && rows[i] is not GroupHeader)
                continue;
            if (BindingList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            double top = container.TranslatePoint(new Point(0, 0), BindingList).Y;
            double bottom = top + container.ActualHeight;
            if (dragged is GroupHeader)
                bottom = SectionBottom(i, bottom);
            if (pointer < bottom)
            {
                bool after = pointer > (top + bottom) / 2;
                return (rows[i], after, after ? bottom : top);
            }
            last = (rows[i], top, bottom);
        }
        return last is { } end ? (end.Item, true, end.Bottom) : null;
    }

    // Bottom edge of a group: its header plus every row under it, up to the next header.
    private double SectionBottom(int headerIndex, double headerBottom)
    {
        double bottom = headerBottom;
        for (int i = headerIndex + 1; i < _viewModel.Rows.Count && _viewModel.Rows[i] is not GroupHeader; i++)
        {
            if (BindingList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row)
                bottom = row.TranslatePoint(new Point(0, row.ActualHeight), BindingList).Y;
        }
        return bottom;
    }

    private void ScrollNearEdges(DragEventArgs e)
    {
        const double edge = 36;
        double y = e.GetPosition(ListScroller).Y;
        if (y < edge)
            ListScroller.ScrollToVerticalOffset(ListScroller.VerticalOffset - 14);
        else if (y > ListScroller.ActualHeight - edge)
            ListScroller.ScrollToVerticalOffset(ListScroller.VerticalOffset + 14);
    }

    private void ShowInsertion(double y)
    {
        if (_insertion is null)
        {
            _insertion = new InsertionAdorner(BindingList, (Brush)FindResource("ClayBrush"));
            AdornerLayer.GetAdornerLayer(BindingList)?.Add(_insertion);
        }
        _insertion.Y = y;
    }

    private void HideInsertion()
    {
        if (_insertion is null)
            return;
        AdornerLayer.GetAdornerLayer(BindingList)?.Remove(_insertion);
        _insertion = null;
    }

    private void OnProfileClick(object sender, MouseButtonEventArgs e)
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

    private string FileFilter => $"{Loc.T("Settings.FileFilter")} (*.json)|*.json";

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Settings.Export"),
            Filter = FileFilter,
            DefaultExt = ".json",
            FileName = $"Clayboard-bindings-{DateTime.Now:yyyy-MM-dd}.json",
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
            var bindings = ConfigStore.ReadBindingsFile(dialog.FileName, out _pendingGroups);
            if (bindings.Count == 0)
            {
                ShowBackupStatus(Loc.T("Settings.ImportNone"), isError: true);
                return;
            }
            // Ask how to merge before changing anything.
            _pendingImport = bindings;
            ImportFoundText.Text = Loc.F("Settings.ImportFound", bindings.Count, Path.GetFileName(dialog.FileName));
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
        ImportChoice.Visibility = Visibility.Collapsed;
    }

    private void FinishImport(bool replace)
    {
        if (_pendingImport is null)
            return;
        var (added, backupPath) = _viewModel.ImportBindings(_pendingImport, _pendingGroups, replace);
        _pendingImport = null;
        ImportChoice.Visibility = Visibility.Collapsed;
        ShowBackupStatus(replace
            ? Loc.F("Settings.ImportReplaced", added, backupPath)
            : Loc.F("Settings.ImportDone", added), isError: false);
    }

    private void ShowBackupStatus(string message, bool isError)
    {
        BackupStatus.Text = message;
        BackupStatus.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "TerraBrush" : "MuteBrush");
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

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", $"/select,\"{_viewModel.ConfigPath}\"");

    private void OnEditConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_viewModel.ConfigPath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No app is registered for .json files.
            Process.Start("notepad.exe", $"\"{_viewModel.ConfigPath}\"");
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
                SyncStartupSwitch();
                SequenceWaitBox.Text = _viewModel.SequenceTimeoutMs.ToString();
                foreach (RadioButton chip in LanguageChips.Children)
                    chip.IsChecked = (string)chip.Tag == Loc.Language;
                ShowPage(SettingsPage);
                break;
        }
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
