using System.Diagnostics;
using System.IO;
using Miri.Core;
using Miri.Pet;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Miri.App;

/// <summary>Notification-area icon: Talk, Feed, Model ▸, Pause/Resume pet, Let Miri tease me, Open data folder, Quit.</summary>
internal sealed partial class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _modelMenu;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly MiriSettings _settings;
    private readonly PetController _pet;
    private readonly Func<IMiriBrain?> _brain;
    private readonly Drawing.Icon _image;
    private readonly IntPtr _hIcon;

    public TrayIcon(SpriteSheet sheet, MiriSettings settings, PetController pet, Func<IMiriBrain?> brain,
                    Action openChat, Action quit)
    {
        _settings = settings;
        _pet = pet;
        _brain = brain;
        _quit = quit;

        (_image, _hIcon) = MakeIcon(sheet[PetAnimation.Sit].Frames[0]);

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        var talk = new Forms.ToolStripMenuItem($"Talk to Miri  ({settings.ChatHotkey})", null, (_, _) => openChat())
        {
            Font = new Drawing.Font(Forms.Control.DefaultFont, Drawing.FontStyle.Bold),
        };
        _modelMenu = new Forms.ToolStripMenuItem("Model");
        _modelMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("(loading...)") { Enabled = false });
        _modelMenu.DropDownOpening += async (_, _) => await FillModelsAsync();
        _pauseItem = new Forms.ToolStripMenuItem("Pause pet", null, (_, _) => TogglePause());
        var feed = new Forms.ToolStripMenuItem("Feed Miri", null, (_, _) => pet.Feed());
        var play = new Forms.ToolStripMenuItem("Play with Miri", null, (_, _) => { if (pet.Playing) pet.StopPlay(); else pet.StartPlay(); });
        var tease = new Forms.ToolStripMenuItem("Let Miri tease me") { Checked = settings.Mischief, CheckOnClick = true };
        tease.CheckedChanged += (_, _) =>
        {
            _settings.Mischief = tease.Checked;
            try { _settings.Save(); } catch { }
        };
        var loc = new Forms.ToolStripMenuItem("Use my location for weather") { Checked = settings.WeatherUseLocation, CheckOnClick = true };
        loc.CheckedChanged += (_, _) =>
        {
            _settings.WeatherUseLocation = loc.Checked;
            try { _settings.Save(); } catch { }
        };
        var pick = new Forms.ToolStripMenuItem("Choose models…", null, (_, _) => new ModelSetupWindow(_settings, brain).Show());
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartWithWindows(startup.Checked);
        var updates = new Forms.ToolStripMenuItem("Check for updates…", null, async (_, _) => await CheckForUpdatesAsync(quietIfNone: false));
        var data = new Forms.ToolStripMenuItem("Open data folder", null, (_, _) => OpenDataFolder());
        var exit = new Forms.ToolStripMenuItem("Quit", null, (_, _) => quit());

        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            talk, feed, play, new Forms.ToolStripSeparator(), _modelMenu, _pauseItem, tease, loc, startup, pick, updates, data, new Forms.ToolStripSeparator(), exit,
        });

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Miri - pixel kitten assistant",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.BalloonTipClicked += async (_, _) => await UpdateClickedAsync();
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) openChat(); };
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Per-user autostart entry (HKCU Run): no admin needed, removed by the uninstaller.</summary>
    private static bool StartsWithWindows
    {
        get { try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Miri") != null; } catch { return false; } }
    }

    private static void SetStartWithWindows(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("Miri", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Miri", throwOnMissingValue: false);
        }
        catch (Exception ex) { AppShell.Log("Start with Windows: " + ex.Message); }
    }

    private UpdateChecker.Release? _update;
    private readonly Action _quit;
    private bool _installing;

    /// <summary>Asks GitHub for a newer release; a balloon says so (click it to open the download page).</summary>
    public async Task CheckForUpdatesAsync(bool quietIfNone)
    {
        try
        {
            var newer = await UpdateChecker.NewerReleaseAsync();
            _settings.LastUpdateCheck = DateTime.Now;
            try { _settings.Save(); } catch { }
            if (newer != null)
            {
                _update = newer;
                ShowBalloon($"Miri {newer.Version} is out!", $"You have {UpdateChecker.Current.ToString(3)}. Click here to " +
                    (newer.SetupUrl != null ? "update, meow." : "open the download page, meow."));
            }
            else if (!quietIfNone) { _update = null; ShowBalloon("Miri is up to date", $"Version {UpdateChecker.Current.ToString(3)} is the newest, meow."); }
        }
        catch (Exception ex)
        {
            AppShell.Log("Update check failed: " + ex.Message);
            if (!quietIfNone) ShowBalloon("Couldn't check for updates", "Is the internet on? Try again later.");
        }
    }

    /// <summary>Balloon clicked: with the user's OK, download the release's Miri-Setup.exe and run it in update mode.</summary>
    private async Task UpdateClickedAsync()
    {
        if (_update is not { } u || _installing) return;
        if (u.SetupUrl == null) { OpenUpdatePage(); return; }   // release without an installer attached
        var ok = Forms.MessageBox.Show($"Update Miri to {u.Version} now?\n\nMiri downloads the installer from GitHub (about 80 MB), closes, and comes back updated. " +
            "Your chats and settings are kept.", "Update Miri", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question);
        if (ok != Forms.DialogResult.Yes) return;
        _installing = true;
        try
        {
            ShowBalloon("Downloading the update…", "Miri will restart by herself when it's ready, meow.");
            var setup = await UpdateChecker.DownloadSetupAsync(u);
            var psi = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
            psi.Environment["MIRI_UPDATE"] = "1";   // install.ps1: no questions, keeps Start with Windows and models as they are
            Process.Start(psi);
            _quit();
        }
        catch (Exception ex)
        {
            AppShell.Log("Update failed: " + ex.Message);
            ShowBalloon("Update failed", "Opening the download page instead, meow.");
            OpenUpdatePage();
        }
        finally { _installing = false; }
    }

    private void OpenUpdatePage()
    {
        var url = _update?.Url;
        if (url == null || !url.StartsWith($"https://github.com/{UpdateChecker.Repo}/", StringComparison.OrdinalIgnoreCase)) return;   // only ever our own release page
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info); } catch { }
    }

    private async Task FillModelsAsync()
    {
        var brain = _brain();
        _modelMenu.DropDownItems.Clear();
        if (brain == null)
        {
            _modelMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("(brain not loaded)") { Enabled = false });
            return;
        }
        var loading = new Forms.ToolStripMenuItem("(asking Ollama...)") { Enabled = false };
        _modelMenu.DropDownItems.Add(loading);

        IReadOnlyList<string> models;
        try { models = await brain.AvailableModelsAsync(); }
        catch { models = Array.Empty<string>(); }

        _modelMenu.DropDownItems.Clear();
        if (models.Count == 0)
        {
            _modelMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("(Ollama not reachable)") { Enabled = false });
            return;
        }
        string active = brain.ActiveModel;
        foreach (var m in models)
        {
            var name = m;
            var item = new Forms.ToolStripMenuItem(name)
            {
                Checked = string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
            };
            item.Click += (_, _) =>
            {
                brain.ActiveModel = name;
                _settings.ChatModel = name;
                try { _settings.Save(); } catch { }
                foreach (Forms.ToolStripMenuItem other in _modelMenu.DropDownItems.OfType<Forms.ToolStripMenuItem>())
                    other.Checked = ReferenceEquals(other, item);
            };
            _modelMenu.DropDownItems.Add(item);
        }
    }

    private void TogglePause()
    {
        _pet.Paused = !_pet.Paused;
        _pauseItem.Text = _pet.Paused ? "Resume pet" : "Pause pet";
        _pauseItem.Checked = _pet.Paused;
    }

    private static void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{MiriSettings.DataDir}\"") { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Tray icon = the kitten's head from the first Sit frame, scaled 2x with nearest neighbour.</summary>
    private static (Drawing.Icon, IntPtr) MakeIcon(SpriteFrame frame)
    {
        // head region: columns 5..22, rows 2..19 (18x18)
        const int cx = 5, cy = 2, n = 18, s = 2;
        using var bmp = new Drawing.Bitmap(n * s, n * s, Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int sx = cx + x, sy = cy + y;
                uint c = (sx < frame.Width && sy < frame.Height) ? frame.Pixels[sy * frame.Width + sx] : 0;
                var color = Drawing.Color.FromArgb(unchecked((int)c));
                for (int dy = 0; dy < s; dy++)
                    for (int dx = 0; dx < s; dx++)
                        bmp.SetPixel(x * s + dx, y * s + dy, color);
            }
        IntPtr h = bmp.GetHicon();
        return (Drawing.Icon.FromHandle(h), h);
    }

    private int _disposed;

    /// <summary>Idempotent and thread-safe: also called from ProcessExit / unhandled-exception paths.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _icon.Visible = false; } catch { }   // removes the notification-area icon (no stale ghost)
        try { _icon.Dispose(); } catch { }
        try { _image.Dispose(); } catch { }
        Win32.DestroyIcon(_hIcon);
    }
}
