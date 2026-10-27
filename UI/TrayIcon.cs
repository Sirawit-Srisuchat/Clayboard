using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MacroInWZa007.Localization;

namespace MacroInWZa007.UI;

/// <summary>Notification-area icon with the app's quick actions.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _reloadItem;
    private readonly ToolStripMenuItem _exitItem;

    public TrayIcon(Action open, Action togglePause, Action reload, Action exit)
    {
        using (var stream = OpenAppIcon())
            _appIcon = new Icon(stream, SystemInformation.SmallIconSize);
        _openItem = new ToolStripMenuItem("", null, (_, _) => open());
        _pauseItem = new ToolStripMenuItem("", null, (_, _) => togglePause());
        _reloadItem = new ToolStripMenuItem("", null, (_, _) => reload());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => exit());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { _openItem, _pauseItem, _reloadItem, new ToolStripSeparator(), _exitItem });

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();

        ApplyTexts();
        Loc.Changed += ApplyTexts;
    }

    /// <summary>The app icon (Assets/app.ico) embedded in the exe, with every size.</summary>
    public static Stream OpenAppIcon() =>
        typeof(TrayIcon).Assembly.GetManifestResourceStream("app.ico")!;

    public void SetPaused(bool paused)
    {
        _pauseItem.Checked = paused;
        ApplyTexts();
    }

    public void ShowInfo(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.None);

    public void ShowError(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning);

    public void Dispose()
    {
        Loc.Changed -= ApplyTexts;
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }

    private void ApplyTexts()
    {
        _openItem.Text = Loc.T("Tray.Open");
        _pauseItem.Text = Loc.T("Tray.PauseAll");
        _reloadItem.Text = Loc.T("Tray.Reload");
        _exitItem.Text = Loc.T("Tray.Exit");
        _icon.Text = _pauseItem.Checked ? Loc.T("Tray.Paused") : "Macro";
    }
}
