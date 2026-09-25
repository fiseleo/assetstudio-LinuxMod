using Avalonia;
using System;
using System.Globalization;
using System.IO;

namespace AssetStudio.Avalonia
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            // Keys.json, Maps/ and the log file are resolved relative to the application folder, as on Windows.
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            NativeLibraries.Register();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
