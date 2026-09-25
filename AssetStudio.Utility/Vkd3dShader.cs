using SpirV;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Translates Direct3D shader byte code (SM2/3 D3D bytecode, SM4/5 DXBC) to Vulkan SPIR-V with
    /// vkd3d-shader (Wine's D3D12-on-Vulkan shader compiler), and SPIR-V to Vulkan GLSL with SPIRV-Cross.
    /// Used instead of d3dcompiler / HLSLDecompiler where those Windows libraries are not available.
    /// The libraries are loaded from the platform folder next to the executable (x64/ or arm64/: libvkd3d-shader.so,
    /// libspirv-cross-c-shared.so), falling back to the system libraries.
    /// </summary>
    public static class Vkd3dShader
    {
        public enum SourceType
        {
            DxbcTpf = 1,
            D3DBytecode = 3,
        }

        private enum TargetType
        {
            SpirvBinary = 1,
            D3DAsm = 3,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderCode
        {
            public IntPtr Code;
            public nuint Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CompileInfo
        {
            public int Type; // VKD3D_SHADER_STRUCTURE_TYPE_COMPILE_INFO
            public IntPtr Next;
            public ShaderCode Source;
            public int SourceType;
            public int TargetType;
            public IntPtr Options;
            public uint OptionCount;
            public int LogLevel;
            public IntPtr SourceName;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CompileFn(ref CompileInfo info, out ShaderCode output, out IntPtr messages);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeCodeFn(ref ShaderCode code);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeMessagesFn(IntPtr messages);

        private const int LogLevelError = 1;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static CompileFn compile;
        private static FreeCodeFn freeCode;
        private static FreeMessagesFn freeMessages;

        public static bool IsAvailable
        {
            get
            {
                Initialize();
                return compile != null;
            }
        }

        private static void Initialize()
        {
            lock (initLock)
            {
                if (initialized)
                    return;
                initialized = true;
                foreach (var candidate in Candidates("vkd3d-shader", "1"))
                {
                    if (!NativeLibrary.TryLoad(candidate, out var handle))
                        continue;
                    if (NativeLibrary.TryGetExport(handle, "vkd3d_shader_compile", out var pCompile)
                        && NativeLibrary.TryGetExport(handle, "vkd3d_shader_free_shader_code", out var pFreeCode)
                        && NativeLibrary.TryGetExport(handle, "vkd3d_shader_free_messages", out var pFreeMessages))
                    {
                        compile = Marshal.GetDelegateForFunctionPointer<CompileFn>(pCompile);
                        freeCode = Marshal.GetDelegateForFunctionPointer<FreeCodeFn>(pFreeCode);
                        freeMessages = Marshal.GetDelegateForFunctionPointer<FreeMessagesFn>(pFreeMessages);
                        Logger.Verbose($"Loaded vkd3d-shader from {candidate}");
                        return;
                    }
                    NativeLibrary.Free(handle);
                }
            }
        }

        internal static IEnumerable<string> Candidates(string name, string soVersion)
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "x64",
            };
            string file = OperatingSystem.IsWindows() ? $"lib{name}-{soVersion}.dll" : OperatingSystem.IsMacOS() ? $"lib{name}.dylib" : $"lib{name}.so";
            yield return Path.Combine(AppContext.BaseDirectory, arch, file);
            yield return Path.Combine(AppContext.BaseDirectory, file);
            yield return OperatingSystem.IsWindows() ? file : OperatingSystem.IsMacOS() ? $"lib{name}.{soVersion}.dylib" : $"lib{name}.so.{soVersion}";
        }

        /// <summary>Converts the shader to Vulkan SPIR-V.</summary>
        public static byte[] ToSpirv(ReadOnlySpan<byte> byteCode, SourceType sourceType)
        {
            return Compile(byteCode, sourceType, TargetType.SpirvBinary);
        }

        /// <summary>Converts the shader to SPIR-V and decompiles that to Vulkan GLSL.</summary>
        public static string ToGlsl(ReadOnlySpan<byte> byteCode, SourceType sourceType)
        {
            return SpirvCross.ToGlsl(ToSpirv(byteCode, sourceType));
        }

        /// <summary>SPIR-V disassembly (managed disassembler, used when SPIRV-Cross is missing).</summary>
        public static string ToSpirvText(ReadOnlySpan<byte> byteCode, SourceType sourceType)
        {
            using var ms = new MemoryStream(ToSpirv(byteCode, sourceType));
            var module = Module.ReadFrom(ms);
            return new Disassembler().Disassemble(module, DisassemblyOptions.Default).Replace("\r\n", "\n");
        }

        /// <summary>Direct3D assembly listing (like D3DDisassemble).</summary>
        public static string ToD3DAsm(ReadOnlySpan<byte> byteCode, SourceType sourceType)
        {
            return Encoding.UTF8.GetString(Compile(byteCode, sourceType, TargetType.D3DAsm)).TrimEnd('\0');
        }

        private static unsafe byte[] Compile(ReadOnlySpan<byte> byteCode, SourceType sourceType, TargetType targetType)
        {
            Initialize();
            if (compile == null)
                throw new DllNotFoundException("vkd3d-shader library not found");
            fixed (byte* p = byteCode)
            {
                var info = new CompileInfo
                {
                    Type = 0,
                    Source = new ShaderCode { Code = (IntPtr)p, Size = (nuint)byteCode.Length },
                    SourceType = (int)sourceType,
                    TargetType = (int)targetType,
                    LogLevel = LogLevelError,
                };
                var ret = compile(ref info, out var output, out var messages);
                string error = messages != IntPtr.Zero ? Marshal.PtrToStringUTF8(messages) : null;
                if (messages != IntPtr.Zero)
                    freeMessages(messages);
                if (ret < 0)
                {
                    throw new Exception($"vkd3d-shader error {ret}{(string.IsNullOrWhiteSpace(error) ? "" : ": " + error.Trim())}");
                }
                try
                {
                    var result = new byte[(int)output.Size];
                    Marshal.Copy(output.Code, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    freeCode(ref output);
                }
            }
        }
    }

    /// <summary>SPIR-V -> Vulkan GLSL decompiler (SPIRV-Cross C API).</summary>
    public static class SpirvCross
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ContextCreateFn(out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ContextDestroyFn(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetLastErrorFn(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ParseSpirvFn(IntPtr context, IntPtr spirv, nuint wordCount, out IntPtr ir);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CreateCompilerFn(IntPtr context, int backend, IntPtr ir, int captureMode, out IntPtr compiler);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CreateOptionsFn(IntPtr compiler, out IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetUintFn(IntPtr options, int option, uint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetBoolFn(IntPtr options, int option, byte value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InstallOptionsFn(IntPtr compiler, IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CompileFn(IntPtr compiler, out IntPtr source);

        private const int BackendGlsl = 1;
        private const int CaptureModeTakeOwnership = 1;
        private const int OptionGlslVersion = 8 | 0x2000000;
        private const int OptionGlslVulkanSemantics = 10 | 0x2000000;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static ContextCreateFn contextCreate;
        private static ContextDestroyFn contextDestroy;
        private static GetLastErrorFn getLastError;
        private static ParseSpirvFn parseSpirv;
        private static CreateCompilerFn createCompiler;
        private static CreateOptionsFn createOptions;
        private static SetUintFn setUint;
        private static SetBoolFn setBool;
        private static InstallOptionsFn installOptions;
        private static CompileFn compile;

        public static bool IsAvailable
        {
            get
            {
                Initialize();
                return compile != null;
            }
        }

        private static T Export<T>(IntPtr handle, string name) where T : Delegate
        {
            return NativeLibrary.TryGetExport(handle, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
        }

        private static void Initialize()
        {
            lock (initLock)
            {
                if (initialized)
                    return;
                initialized = true;
                foreach (var candidate in Vkd3dShader.Candidates("spirv-cross-c-shared", "0"))
                {
                    if (!NativeLibrary.TryLoad(candidate, out var handle))
                        continue;
                    contextCreate = Export<ContextCreateFn>(handle, "spvc_context_create");
                    contextDestroy = Export<ContextDestroyFn>(handle, "spvc_context_destroy");
                    getLastError = Export<GetLastErrorFn>(handle, "spvc_context_get_last_error_string");
                    parseSpirv = Export<ParseSpirvFn>(handle, "spvc_context_parse_spirv");
                    createCompiler = Export<CreateCompilerFn>(handle, "spvc_context_create_compiler");
                    createOptions = Export<CreateOptionsFn>(handle, "spvc_compiler_create_compiler_options");
                    setUint = Export<SetUintFn>(handle, "spvc_compiler_options_set_uint");
                    setBool = Export<SetBoolFn>(handle, "spvc_compiler_options_set_bool");
                    installOptions = Export<InstallOptionsFn>(handle, "spvc_compiler_install_compiler_options");
                    compile = Export<CompileFn>(handle, "spvc_compiler_compile");
                    if (contextCreate != null && contextDestroy != null && getLastError != null && parseSpirv != null && createCompiler != null
                        && createOptions != null && setUint != null && setBool != null && installOptions != null && compile != null)
                    {
                        Logger.Verbose($"Loaded SPIRV-Cross from {candidate}");
                        return;
                    }
                    compile = null;
                    NativeLibrary.Free(handle);
                }
            }
        }

        /// <summary>Decompiles a SPIR-V module to GLSL 450 with Vulkan semantics.</summary>
        public static unsafe string ToGlsl(byte[] spirv)
        {
            Initialize();
            if (compile == null)
                throw new DllNotFoundException("SPIRV-Cross library not found");
            if (spirv.Length % 4 != 0)
                throw new ArgumentException("SPIR-V size is not a multiple of 4");
            if (contextCreate(out var context) != 0)
                throw new Exception("spvc_context_create failed");
            try
            {
                void Check(int result, string what)
                {
                    if (result != 0)
                        throw new Exception($"{what}: {Marshal.PtrToStringUTF8(getLastError(context))}");
                }
                IntPtr ir;
                fixed (byte* p = spirv)
                    Check(parseSpirv(context, (IntPtr)p, (nuint)(spirv.Length / 4), out ir), "SPIR-V parse error");
                Check(createCompiler(context, BackendGlsl, ir, CaptureModeTakeOwnership, out var compiler), "SPIRV-Cross error");
                Check(createOptions(compiler, out var options), "SPIRV-Cross error");
                Check(setUint(options, OptionGlslVersion, 450), "SPIRV-Cross error");
                Check(setBool(options, OptionGlslVulkanSemantics, 1), "SPIRV-Cross error");
                Check(installOptions(compiler, options), "SPIRV-Cross error");
                Check(compile(compiler, out var source), "SPIRV-Cross error");
                return Marshal.PtrToStringUTF8(source);
            }
            finally
            {
                contextDestroy(context);
            }
        }
    }
}
