using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Clayboard.Config;
using Clayboard.Core;
using Clayboard.Localization;
using Clayboard.UI;
using Microsoft.Win32;

namespace Clayboard;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private ConfigStore? _store;
    private HotkeyEngine? _engine;
    private TrayIcon? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _trayHintShown;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Palette.Apply(null); // The original theme until config.json says otherwise.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Loc.Apply(null); // Windows display language until config.json says otherwise.

        // The mutex is only a fast check: anything can create one with this name, so before giving up,
        // confirm a real copy of Macro is running. Otherwise a squatted name could keep Macro from starting.
        _instanceMutex = new Mutex(true, "Clayboard.SingleInstance", out _ownsMutex);
        if (!_ownsMutex && AnotherCopyIsRunning())
        {
            MessageBox.Show(Loc.T("App.AlreadyRunning"), "Clayboard");
            Shutdown();
            return;
        }

        _store = new ConfigStore();
        _engine = HotkeyEngine.Create();
        KeyRecorder.Engine = _engine;
        StartupManager.RefreshPath();
        var viewModel = new MainViewModel(_store, _engine, new TrustStore());
        _viewModel = viewModel;
        viewModel.ThemeChanged += key => Dispatcher.InvokeAsync(() => ApplyTheme(key));
        viewModel.Reload();
        Palette.Apply(viewModel.Theme); // Right away, so the first window opens in the saved theme.

        var tray = new TrayIcon(ShowWindow, () => viewModel.Paused = !viewModel.Paused, viewModel.Reload, ExitApp);
        _tray = tray;
        viewModel.PausedChanged += tray.SetPaused;
        _engine.ActionFailed += (binding, ex) =>
            Dispatcher.InvokeAsync(() => tray.ShowError(Loc.F("App.ActionFailed", binding.Name), ex.Message));
        _store.Changed += () => Dispatcher.InvokeAsync(viewModel.Reload);
        _engine.EmergencyToggle += () => Dispatcher.InvokeAsync(() => viewModel.Paused = !viewModel.Paused);

        try
        {
            _engine.Start();
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(Loc.F("App.HookFailed", ex.Message), "Clayboard");
        }

        if (e.Args.Contains("--tray"))
            TrimMemory();
        else
            ShowWindow();
    }

    private static bool AnotherCopyIsRunning()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(self.ProcessName);
        bool found = others.Any(p => p.Id != self.Id);
        foreach (var process in others)
            process.Dispose();
        return found;
    }

    // With the "system" theme, switching Windows between light, dark, and high contrast restyles the app right away.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle))
            return;
        Dispatcher.InvokeAsync(() =>
        {
            if (Palette.CurrentKey == Palette.SystemKey)
                ApplyTheme(Palette.SystemKey);
        });
    }

    // Open windows repaint in place: their brushes are DynamicResource references to the palette.
    private static void ApplyTheme(string key) => Palette.Apply(key);

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _engine?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        if (_ownsMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowWindow()
    {
        if (_viewModel is null)
            return;
        if (_window is null)
        {
            _window = new MainWindow(_viewModel)
            {
                Icon = BitmapFrame.Create(TrayIcon.OpenAppIcon(), BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
            };
            _window.Closed += OnWindowClosed;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // Closing the window sends Macro to the tray. The window is dropped rather than hidden, which
    // frees most of the memory the UI uses; the tray's Open builds a fresh one.
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        if (_exiting)
            return;
        // Off the UI thread, so closing the window (and opening it again soon after) never waits on the GC.
        Task.Run(TrimMemory);
        if (_trayHintShown)
            return;
        _trayHintShown = true;
        _tray?.ShowInfo(Loc.T("App.StillRunningTitle"), Loc.T("App.StillRunningText"));
    }

    /// <summary>Collects the closed UI and hands unused pages back to Windows while Macro sits in the tray.</summary>
    private static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);

    private void ExitApp()
    {
        _exiting = true;
        _window?.Close();
        Shutdown();
    }
}
