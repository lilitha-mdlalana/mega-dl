using Avalonia;

namespace MegaDl.Desktop;

internal static class Program
{
    // Don't touch Avalonia/UI types before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the IDE previewer — keep it a public static method with this name.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
