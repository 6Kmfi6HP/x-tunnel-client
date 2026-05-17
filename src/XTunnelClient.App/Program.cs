using Avalonia;
using Avalonia.Diagnostics;
using System;
using XTunnelClient.Core;

namespace XTunnelClient.App;

class Program
{
    private static SingleInstanceGuard? s_singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        s_singleInstance = new SingleInstanceGuard(@"Local\x-tunnel-client");
        if (!s_singleInstance.IsPrimary)
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        s_singleInstance.Dispose();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
