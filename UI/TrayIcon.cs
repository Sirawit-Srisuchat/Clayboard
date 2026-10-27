using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacroInWZa007.UI;

/// <summary>Notification-area icon with the app's quick actions.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;

    public TrayIcon(Action open, Action togglePause, Action reload, Action exit)
    {
        IconHandle = DrawIcon();
        _pauseItem = new ToolStripMenuItem("Pause all", null, (_, _) => togglePause());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Macro", null, (_, _) => open());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Reload config", null, (_, _) => reload());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = Icon.FromHandle(IconHandle),
            Text = "Macro",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>HICON shared with the main window.</summary>
    public IntPtr IconHandle { get; }

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
        DestroyIcon(IconHandle);
    }

    /// <summary>Clay rounded square with a cream keycap, matching the title bar mark.</summary>
    private static IntPtr DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var clay = new SolidBrush(Color.FromArgb(0x8B, 0x5E, 0x3C));
            using var cream = new SolidBrush(Color.FromArgb(0xFB, 0xF8, 0xF3));
            using (var outer = RoundedRect(new RectangleF(1, 1, 30, 30), 8))
                g.FillPath(clay, outer);
            using (var key = RoundedRect(new RectangleF(9, 10, 14, 12), 3))
                g.FillPath(cream, key);
        }
        return bitmap.GetHicon();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
