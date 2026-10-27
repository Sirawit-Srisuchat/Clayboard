using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MacroInWZa007.Config;
using MacroInWZa007.Core;
using MacroInWZa007.Localization;

namespace MacroInWZa007.UI;

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
