using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AssetStudio.PInvoke
{
    public static class DllLoader
    {

        public static void PreloadDll(string dllName)
        {
            var dllDir = GetDirectedDllDirectory();

            // Not using OperatingSystem.Platform.
            // See: https://www.mono-project.com/docs/faq/technical/#how-to-detect-the-execution-platform
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Win32.LoadDll(dllDir, dllName);
            }
            else
            {
                Posix.LoadDll(dllDir, dllName);
            }
        }

        private static string GetDirectedDllDirectory()
        {
            // AppContext.BaseDirectory is the application folder even when started through `dotnet app.dll`,
            // where the main module would be the dotnet host instead.
            var localDir = AppContext.BaseDirectory;

            var subDir = Environment.Is64BitProcess ? "x64" : "x86";

            var directedDllDir = Path.Combine(localDir, subDir);

            return directedDllDir;
        }

        private static partial class Win32
        {

            internal static void LoadDll(string dllDir, string dllName)
            {
                var dllFileName = $"{dllName}.dll";
                var directedDllPath = Path.Combine(dllDir, dllFileName);

                // Specify SEARCH_DLL_LOAD_DIR to load dependent libraries located in the same platform-specific directory.
                var hLibrary = LoadLibraryEx(directedDllPath, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);

                if (hLibrary == IntPtr.Zero)
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    var exception = new Win32Exception(errorCode);

                    throw new DllNotFoundException(exception.Message, exception);
                }
            }

            // HMODULE LoadLibraryExA(LPCSTR lpLibFileName, HANDLE hFile, DWORD dwFlags);
            // HMODULE LoadLibraryExW(LPCWSTR lpLibFileName, HANDLE hFile, DWORD dwFlags);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

            private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;
            private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x100;

        }

        private static class Posix
        {

            internal static void LoadDll(string dllDir, string dllName)
            {
                string dllExtension;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    dllExtension = ".so";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    dllExtension = ".dylib";
                }
                else
                {
                    throw new NotSupportedException();
                }

                var dllFileName = $"lib{dllName}{dllExtension}";
                var directedDllPath = Path.Combine(dllDir, dllFileName);

                // NativeLibrary.Load instead of P/Invoking dlopen from "libdl": most systems only ship
                // libdl.so.2 (no unversioned libdl.so without dev packages), and since glibc 2.34 dlopen lives in libc.
                // Once loaded, [DllImport(dllName)] resolves to this library through its SONAME.
                NativeLibrary.Load(directedDllPath);
            }

        }

    }
}
