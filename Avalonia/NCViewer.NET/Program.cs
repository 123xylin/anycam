using System;
using Avalonia;

namespace NcViewerNet;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AnyCAD.Foundation.GlobalInstance.Initialize();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        AnyCAD.Foundation.GlobalInstance.Destroy();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
