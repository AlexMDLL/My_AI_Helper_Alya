using Avalonia;

namespace Alya.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
