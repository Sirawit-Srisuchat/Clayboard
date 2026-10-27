using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MacroInWZa007.Config;
using MacroInWZa007.Core;
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

        _instanceMutex = new Mutex(true, "MacroInWZa007.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("Macro is already running. Look for it in the system tray.", "Macro");
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
            Dispatcher.InvokeAsync(() => tray.ShowError($"\"{binding.Name}\" didn't run", ex.Message));
        _store.Changed += () => Dispatcher.InvokeAsync(viewModel.Reload);

        try
        {
            _engine.Start();
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show($"Couldn't start listening to the keyboard. {ex.Message}", "Macro");
        }

        _window = new MainWindow(viewModel)
        {
            Icon = Imaging.CreateBitmapSourceFromHIcon(tray.IconHandle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions()),
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
        _tray?.ShowInfo("Macro is still running", "Your hotkeys keep working. Open Macro from the tray icon.");
    }

    private void ExitApp()
    {
        if (_window is not null)
            _window.AllowClose = true;
        Shutdown();
    }
}
