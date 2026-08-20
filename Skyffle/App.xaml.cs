using System.IO;
using Microsoft.UI.Xaml;

namespace Skyffle;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    private static readonly string CrashLog =
        Path.Combine(Path.GetTempPath(), "skyffle-crash.txt");

    public App()
    {
        UnhandledException += (_, e) =>
        {
            Log($"XAML UnhandledException: {e.Message}\n{e.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"AppDomain UnhandledException: {e.ExceptionObject}");
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            Log($"OnLaunched failed: {ex}");
            throw;
        }
    }

    public static void Log(string message)
    {
        try { File.AppendAllText(CrashLog, $"[{DateTime.Now:HH:mm:ss}] {message}\n"); } catch { }
    }
}
