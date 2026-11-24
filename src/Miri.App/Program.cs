using System.IO;

namespace Miri.App;

/// <summary>
/// Entry point (instead of WPF's generated Main) so a crash before App.OnStartup, which happens on some
/// PCs, still leaves its reason in %LOCALAPPDATA%\Miri-crash.log for the installer to show.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record(e.ExceptionObject);
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            Record(ex);
            throw;
        }
    }

    private static void Record(object error)
    {
        try
        {
            File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Miri-crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Windows {Environment.OSVersion.Version}{Environment.NewLine}{error}");
        }
        catch { }
    }
}
