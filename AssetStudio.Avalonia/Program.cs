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
            // Keys.json, Maps/ and the log file live in the application folder, as on Windows (Maps/ relative to the
            // current directory), or in ~/.local/share/AssetStudio when that folder is read-only (AppImage, /opt install).
            AppData.Directory = FindDataDirectory();
            Directory.SetCurrentDirectory(AppData.Directory);
            NativeLibraries.Register();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        private static string FindDataDirectory()
        {
            var appDirectory = AppContext.BaseDirectory;
            if (IsWritable(appDirectory))
                return appDirectory;
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome))
                dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            var dataDirectory = Path.Combine(dataHome, "AssetStudio");
            Directory.CreateDirectory(dataDirectory);
            // start from the shipped key list, then keep the user's edits
            var keys = Path.Combine(dataDirectory, UnityCNManager.KeysFileName);
            var shippedKeys = Path.Combine(appDirectory, UnityCNManager.KeysFileName);
            if (!File.Exists(keys) && File.Exists(shippedKeys))
                File.Copy(shippedKeys, keys);
            return dataDirectory;
        }

        private static bool IsWritable(string directory)
        {
            try
            {
                var probe = Path.Combine(directory, $".write_test_{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
