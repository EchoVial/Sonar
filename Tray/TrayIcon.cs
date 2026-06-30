using System.Drawing;
using System.Windows.Forms;
using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Startup;

namespace SpotifyLyricsTaskbar.Tray;

/// <summary>Sonar tray icon: left-click opens Settings; right-click is a small menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppConfig _config;
    private readonly App _app;
    private readonly NotifyIcon _icon;

    public TrayIcon(AppConfig config, App app)
    {
        _config = config;
        _app = app;
        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Sonar",
            Visible = false,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _app.OpenSettings(); };
        _icon.DoubleClick += (_, _) => _app.OpenSettings();
    }

    public void Show() => _icon.Visible = true;

    private static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/sonarlogo.png", UriKind.Absolute));
            if (info != null)
            {
                using var src = new Bitmap(info.Stream);
                using var sized = new Bitmap(src, new Size(32, 32));
                return Icon.FromHandle(sized.GetHicon());
            }
        }
        catch { /* fall back */ }
        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => _app.OpenSettings();
        menu.Items.Add(settings);

        var enabled = new ToolStripMenuItem("Enabled") { Checked = _config.Enabled, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) => _app.SetEnabled(enabled.Checked);
        menu.Items.Add(enabled);

        var startup = new ToolStripMenuItem("Run at startup") { Checked = AutoStart.IsEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => { AutoStart.Set(startup.Checked); _config.RunAtStartup = startup.Checked; _config.Save(); };
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => _app.QuitApp();
        menu.Items.Add(quit);

        return menu;
    }

    public void Dispose()
    {
        try { _icon.Visible = false; _icon.Dispose(); } catch { /* ignore */ }
    }
}
