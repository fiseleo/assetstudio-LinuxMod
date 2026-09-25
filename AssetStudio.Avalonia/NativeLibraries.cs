using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AssetStudio.Avalonia
{
    /// <summary>
    /// Locates optional native libraries (FMOD, FBX exporter, ACL, ...) in the platform sub-folder
    /// (x64/x86/arm64 next to the executable) and tells .NET where to find them.
    /// On Linux these are expected as lib&lt;name&gt;.so, e.g. x64/libfmod.so.
    /// </summary>
    public static class NativeLibraries
    {
        public static string NativeDirectory => Path.Combine(AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        });

        public static string FmodLibraryFileName => LibraryFileName(Environment.Is64BitProcess && OperatingSystem.IsWindows() ? "fmod64" : "fmod");
        public static string FbxLibraryFileName => LibraryFileName("AssetStudio.FBXNative");

        public static bool FmodAvailable => Find(FmodLibraryFileName) != null || Find(LibraryFileName("fmod")) != null;
        public static bool FbxAvailable => Find(FbxLibraryFileName) != null;

        public static string LibraryFileName(string name)
        {
            if (OperatingSystem.IsWindows())
                return $"{name}.dll";
            if (OperatingSystem.IsMacOS())
                return $"lib{name}.dylib";
            return $"lib{name}.so";
        }

        private static string Find(string fileName)
        {
            foreach (var dir in new[] { NativeDirectory, AppContext.BaseDirectory })
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        public static void Register()
        {
            foreach (var assembly in new[] { typeof(AssetStudio.Fbx).Assembly, typeof(AssetStudio.AudioClipConverter).Assembly })
            {
                try
                {
                    NativeLibrary.SetDllImportResolver(assembly, Resolve);
                }
                catch (InvalidOperationException)
                {
                    // resolver already set
                }
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            var name = Path.GetFileNameWithoutExtension(libraryName);
            if (name.StartsWith("lib") && !OperatingSystem.IsWindows())
                name = name[3..];
            foreach (var candidate in new[] { LibraryFileName(name), libraryName })
            {
                var path = Find(candidate);
                if (path != null && NativeLibrary.TryLoad(path, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        }
    }
}
