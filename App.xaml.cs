using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using MacroInWZa007.Config;
using MacroInWZa007.Core;
using MacroInWZa007.Localization;
using MacroInWZa007.UI;

namespace MacroInWZa007;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private ConfigStore? _store;
    private HotkeyEngine? _engine;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Loc.Apply(null); // Windows display language until config.json says otherwise.

        _instanceMutex = new Mutex(true, "MacroInWZa007.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show(Loc.T("App.AlreadyRunning"), "Macro");
            Shutdown();
            return;
        }

        _store = new ConfigStore();
        _engine = new HotkeyEngine();
        KeyRecorder.Engine = _engine;
        StartupManager.RefreshPath();
        var viewModel = new MainViewModel(_store, _engine);
        viewModel.Reload();

        var tray = new TrayIcon(ShowWindow, () => viewModel.Paused = !viewModel.Paused, viewModel.Reload, ExitApp);
        _tray = tray;
        viewModel.PausedChanged += tray.SetPaused;
        _engine.ActionFailed += (binding, ex) =>
            Dispatcher.InvokeAsync(() => tray.ShowError(Loc.F("App.ActionFailed", binding.Name), ex.Message));
        _store.Changed += () => Dispatcher.InvokeAsync(viewModel.Reload);

        try
        {
            _engine.Start();
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(Loc.F("App.HookFailed", ex.Message), "Macro");
        }

        _window = new MainWindow(viewModel)
        {
            Icon = BitmapFrame.Create(TrayIcon.OpenAppIcon(), BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
        };
        _window.HiddenToTray += OnHiddenToTray;
        if (!e.Args.Contains("--tray"))
            _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnHiddenToTray()
    {
        if (_trayHintShown)
            return;
        _trayHintShown = true;
        _tray?.ShowInfo(Loc.T("App.StillRunningTitle"), Loc.T("App.StillRunningText"));
    }

    private void ExitApp()
    {
        if (_window is not null)
            _window.AllowClose = true;
        Shutdown();
    }
}
