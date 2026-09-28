using Avalonia;
using Avalonia.Media;
using System;

namespace TdPi.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "Microsoft YaHei UI",
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Segoe UI") },
                },
            })
            .LogToTrace();
}
