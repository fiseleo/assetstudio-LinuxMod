using System.Runtime.InteropServices;
using System.Text;

namespace AssetStudio.Tests
{
    internal static class TestUtil
    {
        public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        public static string TempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "assetstudio-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Unity's aligned string: length, bytes, padding to 4.</summary>
        public static void WriteAligned(BinaryWriter writer, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            while (writer.BaseStream.Position % 4 != 0)
                writer.Write((byte)0);
        }
    }

    /// <summary>HLSL to DXBC with vkd3d-shader (the library the application ships), for shader tests.</summary>
    internal static unsafe class Hlsl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Code { public IntPtr Data; public nuint Size; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CompileInfo
        {
            public int Type; public IntPtr Next; public Code Source; public int SourceType; public int TargetType;
            public IntPtr Options; public uint OptionCount; public int LogLevel; public IntPtr SourceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HlslSourceInfo { public int Type; public IntPtr Next; public IntPtr EntryPoint; public Code Secondary; public IntPtr Profile; }

        private delegate int CompileFn(ref CompileInfo info, out Code output, out IntPtr messages);
        private static CompileFn compile;

        public static bool Available
        {
            get
            {
                if (compile != null)
                    return true;
                foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "x64", "libvkd3d-shader.so"), "libvkd3d-shader.so.1" })
                {
                    if (NativeLibrary.TryLoad(path, out var handle) && NativeLibrary.TryGetExport(handle, "vkd3d_shader_compile", out var p))
                    {
                        compile = Marshal.GetDelegateForFunctionPointer<CompileFn>(p);
                        return true;
                    }
                }
                return false;
            }
        }

        public static byte[] ToDxbc(string source, string profile, string entry = "main")
        {
            if (!Available)
                throw new InvalidOperationException("vkd3d-shader not available");
            var sourceBytes = Encoding.UTF8.GetBytes(source);
            var entryPtr = Marshal.StringToHGlobalAnsi(entry);
            var profilePtr = Marshal.StringToHGlobalAnsi(profile);
            try
            {
                fixed (byte* p = sourceBytes)
                {
                    var hlsl = new HlslSourceInfo { Type = 6, EntryPoint = entryPtr, Profile = profilePtr };
                    var info = new CompileInfo
                    {
                        Type = 0, Next = (IntPtr)(&hlsl), Source = new Code { Data = (IntPtr)p, Size = (nuint)sourceBytes.Length },
                        SourceType = 2, TargetType = 5, LogLevel = 1,
                    };
                    var result = compile(ref info, out var output, out var messages);
                    if (result < 0)
                        throw new Exception("HLSL compile error: " + Marshal.PtrToStringUTF8(messages));
                    var bytes = new byte[(int)output.Size];
                    Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                    return bytes;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(entryPtr);
                Marshal.FreeHGlobal(profilePtr);
            }
        }
    }
}
