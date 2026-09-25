using System;

namespace AssetStudio
{
    /// <summary>
    /// Folder of Keys.json and the log file: the application folder by default. A front end can redirect it,
    /// e.g. the Linux GUI when the application folder is read-only (AppImage, system-wide install).
    /// </summary>
    public static class AppData
    {
        public static string Directory { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
    }
}
