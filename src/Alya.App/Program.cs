using Avalonia;

namespace Alya.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!global::Alya.App.AdminCheck.IsElevated())
        {
            try
            {
                global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { Verb = "runas", UseShellExecute = true });
            }
            catch
            {
                // пользователь отклонил UAC — работаем как есть
            }
            return;
        }
        using var mutex = new global::System.Threading.Mutex(true, "AlyaNextSingleInstance", out var created);
        if (!created)
        {
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
