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
    /// The libraries are loaded from the platform folder next to the executable (x64/libvkd3d-shader.so,
    /// x64/libspirv-cross-c-shared.so), falling back to the system libraries.
    /// </summary>
    public static class Vkd3dShader
    {
        public enum SourceType
        {
            DxbcTpf = 1,
            D3DBytecode = 3,
            DxbcDxil = 4,
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

    /// <summary>
    /// GLSL -> SPIR-V compiler (glslang C API): the OpenGL (ES) programs of Unity compiled for Vulkan with the relaxed
    /// rules (the loose uniforms in a default uniform block), bindings and locations assigned, the stages linked.
    /// </summary>
    public static class Glslang
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public int Language, Stage, Client, ClientVersion, TargetLanguage, TargetLanguageVersion;
            public IntPtr Code;
            public int DefaultVersion, DefaultProfile, ForceDefaultVersionAndProfile, ForwardCompatible, Messages;
            public IntPtr Resource;
            public IntPtr IncludeSystem, IncludeLocal, FreeIncludeResult, CallbacksContext;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeProcessFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DefaultResourceFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ShaderCreateFn(ref Input input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShaderDeleteFn(IntPtr shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShaderSetOptionsFn(IntPtr shader, int options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShaderStepFn(IntPtr shader, ref Input input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetLogFn(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ProgramCreateFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProgramDeleteFn(IntPtr program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProgramAddShaderFn(IntPtr program, IntPtr shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgramLinkFn(IntPtr program, int messages);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgramMapIoFn(IntPtr program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProgramGenerateFn(IntPtr program, int stage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ProgramSpirvSizeFn(IntPtr program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProgramSpirvGetFn(IntPtr program, IntPtr words);

        public const int StageVertex = 0, StageFragment = 4;
        private const int SourceGlsl = 1, ClientVulkan = 1, TargetVulkan10 = 1 << 22, TargetSpv = 1, TargetSpv10 = 1 << 16;
        private const int ProfileNone = 1 << 0, ProfileCore = 1 << 1, ProfileEs = 1 << 3;
        private const int MessagesSpvRules = 1 << 3, MessagesVulkanRules = 1 << 4, MessagesSuppressWarnings = 1 << 1;
        private const int OptionAutoMapBindings = 1 << 0, OptionAutoMapLocations = 1 << 1, OptionVulkanRulesRelaxed = 1 << 2;

        private static readonly object compileLock = new object();
        private static bool initialized;
        private static InitializeProcessFn initializeProcess;
        private static DefaultResourceFn defaultResource;
        private static ShaderCreateFn shaderCreate;
        private static ShaderDeleteFn shaderDelete;
        private static ShaderSetOptionsFn shaderSetOptions;
        private static ShaderStepFn shaderPreprocess, shaderParse;
        private static GetLogFn shaderLog, programLog, programSpirvMessages;
        private static ProgramCreateFn programCreate;
        private static ProgramDeleteFn programDelete;
        private static ProgramAddShaderFn programAddShader;
        private static ProgramLinkFn programLink;
        private static ProgramMapIoFn programMapIo;
        private static ProgramGenerateFn programGenerate;
        private static ProgramSpirvSizeFn programSpirvSize;
        private static ProgramSpirvGetFn programSpirvGet;

        public static bool IsAvailable
        {
            get
            {
                Initialize();
                return programSpirvGet != null;
            }
        }

        private static T Export<T>(IntPtr handle, string name) where T : Delegate
        {
            return NativeLibrary.TryGetExport(handle, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
        }

        private static void Initialize()
        {
            lock (compileLock)
            {
                if (initialized)
                    return;
                initialized = true;
                foreach (var candidate in Vkd3dShader.Candidates("glslang", "16"))
                {
                    if (!NativeLibrary.TryLoad(candidate, out var handle))
                        continue;
                    initializeProcess = Export<InitializeProcessFn>(handle, "glslang_initialize_process");
                    defaultResource = Export<DefaultResourceFn>(handle, "glslang_default_resource");
                    shaderCreate = Export<ShaderCreateFn>(handle, "glslang_shader_create");
                    shaderDelete = Export<ShaderDeleteFn>(handle, "glslang_shader_delete");
                    shaderSetOptions = Export<ShaderSetOptionsFn>(handle, "glslang_shader_set_options");
                    shaderPreprocess = Export<ShaderStepFn>(handle, "glslang_shader_preprocess");
                    shaderParse = Export<ShaderStepFn>(handle, "glslang_shader_parse");
                    shaderLog = Export<GetLogFn>(handle, "glslang_shader_get_info_log");
                    programCreate = Export<ProgramCreateFn>(handle, "glslang_program_create");
                    programDelete = Export<ProgramDeleteFn>(handle, "glslang_program_delete");
                    programAddShader = Export<ProgramAddShaderFn>(handle, "glslang_program_add_shader");
                    programLink = Export<ProgramLinkFn>(handle, "glslang_program_link");
                    programMapIo = Export<ProgramMapIoFn>(handle, "glslang_program_map_io");
                    programGenerate = Export<ProgramGenerateFn>(handle, "glslang_program_SPIRV_generate");
                    programSpirvSize = Export<ProgramSpirvSizeFn>(handle, "glslang_program_SPIRV_get_size");
                    programSpirvGet = Export<ProgramSpirvGetFn>(handle, "glslang_program_SPIRV_get");
                    programLog = Export<GetLogFn>(handle, "glslang_program_get_info_log");
                    programSpirvMessages = Export<GetLogFn>(handle, "glslang_program_SPIRV_get_messages");
                    if (initializeProcess != null && defaultResource != null && shaderCreate != null && shaderDelete != null && shaderSetOptions != null
                        && shaderPreprocess != null && shaderParse != null && shaderLog != null && programCreate != null && programDelete != null
                        && programAddShader != null && programLink != null && programMapIo != null && programGenerate != null && programSpirvSize != null
                        && programSpirvGet != null && programLog != null && initializeProcess() != 0)
                    {
                        Logger.Verbose($"Loaded glslang from {candidate}");
                        return;
                    }
                    programSpirvGet = null;
                    NativeLibrary.Free(handle);
                }
            }
        }

        /// <summary>
        /// Compiles the vertex and fragment GLSL of a program (each with its #version) to linked SPIR-V modules; the
        /// errors are thrown with glslang's log.
        /// </summary>
        public static (byte[] Vertex, byte[] Fragment) Compile(string vertexSource, string fragmentSource)
        {
            Initialize();
            if (programSpirvGet == null)
                throw new DllNotFoundException("glslang library not found");
            lock (compileLock)
            {
                var shaders = new List<IntPtr>();
                var sources = new List<IntPtr>();
                var program = programCreate();
                try
                {
                    foreach (var (stage, source) in new[] { (StageVertex, vertexSource), (StageFragment, fragmentSource) })
                    {
                        var code = Marshal.StringToCoTaskMemUTF8(source);
                        sources.Add(code);
                        var es = source.Contains(" es", StringComparison.Ordinal);
                        var input = new Input
                        {
                            Language = SourceGlsl,
                            Stage = stage,
                            Client = ClientVulkan,
                            ClientVersion = TargetVulkan10,
                            TargetLanguage = TargetSpv,
                            TargetLanguageVersion = TargetSpv10,
                            Code = code,
                            DefaultVersion = es ? 310 : 450,
                            DefaultProfile = es ? ProfileEs : ProfileCore,
                            Messages = MessagesSpvRules | MessagesVulkanRules | MessagesSuppressWarnings,
                            Resource = defaultResource(),
                        };
                        var shader = shaderCreate(ref input);
                        if (shader == IntPtr.Zero)
                            throw new Exception("glslang_shader_create failed");
                        shaders.Add(shader);
                        shaderSetOptions(shader, OptionAutoMapBindings | OptionAutoMapLocations | OptionVulkanRulesRelaxed);
                        if (shaderPreprocess(shader, ref input) == 0 || shaderParse(shader, ref input) == 0)
                            throw new Exception($"{(stage == StageVertex ? "vertex" : "fragment")} program: {Marshal.PtrToStringUTF8(shaderLog(shader))}".TrimEnd());
                        programAddShader(program, shader);
                    }
                    if (programLink(program, MessagesSpvRules | MessagesVulkanRules) == 0 || programMapIo(program) == 0)
                        throw new Exception($"link: {Marshal.PtrToStringUTF8(programLog(program))}".TrimEnd());
                    byte[] Generate(int stage)
                    {
                        programGenerate(program, stage);
                        var size = (int)programSpirvSize(program);
                        var words = new byte[size * 4];
                        var handle = GCHandle.Alloc(words, GCHandleType.Pinned);
                        try
                        {
                            programSpirvGet(program, handle.AddrOfPinnedObject());
                        }
                        finally
                        {
                            handle.Free();
                        }
                        return words;
                    }
                    return (Generate(StageVertex), Generate(StageFragment));
                }
                finally
                {
                    programDelete(program);
                    foreach (var shader in shaders)
                        shaderDelete(shader);
                    foreach (var code in sources)
                        Marshal.FreeCoTaskMem(code);
                }
            }
        }
    }

    /// <summary>WGSL -> SPIR-V compiler (naga, through the C interface of linux/naga-c): the WebGPU programs of Unity.</summary>
    public static class Naga
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CompileFn(IntPtr source, uint stage, IntPtr entry, out IntPtr words, out nuint count, out IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFn(IntPtr words, nuint count, IntPtr error);

        public const uint StageVertex = 0, StageFragment = 1;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static CompileFn compile;
        private static FreeFn free;

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
                foreach (var candidate in Vkd3dShader.Candidates("naga_c", "0"))
                {
                    if (!NativeLibrary.TryLoad(candidate, out var handle))
                        continue;
                    if (NativeLibrary.TryGetExport(handle, "naga_wgsl_to_spirv", out var pCompile) && NativeLibrary.TryGetExport(handle, "naga_free", out var pFree))
                    {
                        compile = Marshal.GetDelegateForFunctionPointer<CompileFn>(pCompile);
                        free = Marshal.GetDelegateForFunctionPointer<FreeFn>(pFree);
                        Logger.Verbose($"Loaded naga from {candidate}");
                        return;
                    }
                    NativeLibrary.Free(handle);
                }
            }
        }

        /// <summary>The SPIR-V of an entry point of a WGSL module (the names kept); errors are thrown with naga's message.</summary>
        public static byte[] ToSpirv(string wgsl, uint stage, string entryPoint = "main")
        {
            Initialize();
            if (compile == null)
                throw new DllNotFoundException("naga library not found");
            var source = Marshal.StringToCoTaskMemUTF8(wgsl);
            var entry = Marshal.StringToCoTaskMemUTF8(entryPoint);
            try
            {
                var result = compile(source, stage, entry, out var words, out var count, out var error);
                try
                {
                    if (result != 0)
                        throw new Exception($"{(stage == StageVertex ? "vertex" : "fragment")} program: {Marshal.PtrToStringUTF8(error)}");
                    var spirv = new byte[(int)count * 4];
                    Marshal.Copy(words, spirv, 0, spirv.Length);
                    return spirv;
                }
                finally
                {
                    free(words, count, error);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(source);
                Marshal.FreeCoTaskMem(entry);
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
