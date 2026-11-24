using System.IO;
using System.Windows;
using System.Windows.Threading;
using Miri.Core;
using Miri.Pet;

namespace Miri.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private PetWindow? _petWindow;
    private BubbleWindow? _bubble;
    private HighlightWindow? _highlight;
    private AppShell? _shell;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private Composition.Parts? _parts;
    private Miri.Hands.UiaSurfaceFinder? _surfaces;
    private Mischief? _mischief;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, @"Local\Miri.PixelKitten.SingleInstance", out bool first);
        if (!first)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppShell.Log("Unhandled UI exception: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppShell.Log("Unobserved task exception: " + args.Exception);
            args.SetObserved();
        };

        var settings = MiriSettings.Load();
        string exeDir = AppContext.BaseDirectory;

        // sprites: user-edited png+json next to the exe wins; otherwise generate and export once
        string exportDir = SpriteFactory.FindRepoSpriteDir(exeDir) ?? Path.Combine(exeDir, "assets", "sprites");
        var sheet = SpriteFactory.LoadOrCreate(exeDir, exportDir);

        var pet = new PetController(sheet, settings.Scale);
        try { pet.SurfaceFinder = _surfaces = new Miri.Hands.UiaSurfaceFinder(); }   // drop onto video players etc.
        catch (Exception ex) { AppShell.Log("Surface finder unavailable: " + ex.Message); }
        _petWindow = new PetWindow(pet);
        _petWindow.Show();                       // sets ScreenMetrics from the real DPI

        _bubble = new BubbleWindow();
        IntPtr bubbleHwnd = _bubble.Handle;      // created now so the brain can exclude it
        _highlight = new HighlightWindow();

        _shell = new AppShell(settings, pet, _bubble, _highlight);
        try
        {
            _parts = Composition.Create(settings, _shell, _shell, bubbleHwnd);
            _shell.AttachBrain(_parts.Brain);
        }
        catch (Exception ex)
        {
            _shell.ReportStartupError("My brain didn't start", ex);
        }

        _tray = new TrayIcon(sheet, settings, pet, () => _shell.Brain, _shell.OpenChat, Quit);
        // Best effort: never leave a stale tray icon behind when the process dies without OnExit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeTray();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppShell.Log("Unhandled exception: " + args.ExceptionObject);
            if (args.IsTerminating) DisposeTray();
        };

        _hotkeys = new HotkeyManager(_petWindow.Handle);
        var problems = new List<string>();
        if (!_hotkeys.Register(settings.ChatHotkey, _shell.OpenChat, out var err1))
            problems.Add($"Chat hotkey: {err1} Click me or the tray icon to chat instead.");
        if (!_hotkeys.Register(settings.StopHotkey, _shell.StopCurrent, out var err2))
            problems.Add($"Stop hotkey: {err2}");
        if (problems.Count > 0)
        {
            string fix = $"To pick another combo, edit \"ChatHotkey\" / \"StopHotkey\" in {MiriSettings.SettingsPath} and restart Miri.";
            _tray.ShowBalloon("Miri: hotkey problem", string.Join("\n", problems) + "\n" + fix);
            _shell.ShowNote(string.Join(" ", problems) + " " + fix);
            AppShell.Log("Hotkey registration failed: " + string.Join(" | ", problems));
        }

        pet.Start();
        var shell = _shell;
        _mischief = new Mischief(settings, pet, () => shell.IsBusy || shell.BubbleOpen);
        _mischief.Start();
        if (!settings.ModelSetupDone) new ModelSetupWindow(settings, () => shell.Brain).Show();   // first start: pick models for this PC
    }

    private void DisposeTray()
    {
        try { _tray?.Dispose(); } catch { }
    }

    private void Quit()
    {
        if (_bubble != null) _bubble.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _hotkeys?.Dispose(); } catch { }
        DisposeTray();
        if (_parts != null)
            foreach (var d in _parts.Disposables)
                try { d.Dispose(); } catch { }
        try { _surfaces?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
