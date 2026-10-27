using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MacroInWZa007.UI;

/// <summary>Notification-area icon with the app's quick actions.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private readonly ToolStripMenuItem _pauseItem;

    public TrayIcon(Action open, Action togglePause, Action reload, Action exit)
    {
        using (var stream = OpenAppIcon())
            _appIcon = new Icon(stream, SystemInformation.SmallIconSize);
        _pauseItem = new ToolStripMenuItem("Pause all", null, (_, _) => togglePause());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Macro", null, (_, _) => open());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Reload config", null, (_, _) => reload());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Macro",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>The app icon (Assets/app.ico) embedded in the exe, with every size.</summary>
    public static Stream OpenAppIcon() =>
        typeof(TrayIcon).Assembly.GetManifestResourceStream("app.ico")!;

    public void SetPaused(bool paused)
    {
        _pauseItem.Checked = paused;
        _icon.Text = paused ? "Macro (paused)" : "Macro";
    }

    public void ShowInfo(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.None);

    public void ShowError(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
