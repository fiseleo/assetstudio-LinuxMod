using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Matrix = System.Numerics.Matrix4x4;
using Vec3 = System.Numerics.Vector3;

namespace AssetStudio
{
    /// <summary>A constant buffer of a program as Unity describes it: its register and its fields by byte offset.</summary>
    public sealed class UnityConstantBuffer
    {
        public string Name;
        public int Register;
        public int Size;
        public List<UnityShaderField> Fields = new List<UnityShaderField>();
    }

    public sealed class UnityShaderField
    {
        public string Name;
        public int Offset;
        public int ArraySize; //0: not an array
        public bool IsMatrix;
        public int Components; //vector dimension, or the rows of a matrix
    }

    public sealed class UnityShaderTexture
    {
        public string Name;
        public int Register;
        public int SamplerRegister;
        public int Dimension; //2 = 2D, 3 = 3D, 4 = cube, 5 = 2D array, 6 = cube array
    }

    /// <summary>One stage (vertex or fragment) of a variant: its byte code and what it reads.</summary>
    public sealed class UnityShaderStage
    {
        public ShaderGpuProgramType ProgramType;
        public byte[] Dxbc;
        /// <summary>
        /// Vulkan: the SPIR-V of the stage; the resources keep Unity's sets and bindings, the registers of the parameters
        /// are (stages &lt;&lt; 24) | (set &lt;&lt; 16) | binding.
        /// </summary>
        public byte[] Spirv;
        /// <summary>Vulkan: the vertex inputs, Unity's shader channel and the input location.</summary>
        public List<(int Channel, int Location)> Inputs = new List<(int, int)>();
        /// <summary>OpenGL (ES): the GLSL of the stage (see <see cref="UnityGlsl"/>), compiled by glslang.</summary>
        public string Glsl;
        /// <summary>WebGPU: the WGSL of the stage, compiled by naga.</summary>
        public string Wgsl;
        public List<UnityConstantBuffer> ConstantBuffers = new List<UnityConstantBuffer>();
        public List<UnityShaderTexture> Textures = new List<UnityShaderTexture>();
    }

    /// <summary>The render state of a pass (values taken from the material where the shader names a property).</summary>
    public sealed class UnityRenderState
    {
        public int Cull = 2; //0 off, 1 front, 2 back
        public bool ZWrite = true;
        public int ZTest = 4; //CompareFunction: 4 LessEqual
        public bool Blend;
        public int SrcBlend = 1, DstBlend = 0, SrcBlendAlpha = 1, DstBlendAlpha = 0;
        public int BlendOp, BlendOpAlpha;
        public int ColorMask = 15;
    }

    /// <summary>
    /// A variant of a shader, chosen for a material: the forward pass of the first sub shader with Direct3D 11 programs,
    /// the vertex and fragment programs whose keywords fit the material's.
    /// </summary>
    public sealed class UnityShaderVariant
    {
        public string ShaderName;
        public string PassName;
        public string LightMode;
        public string[] Keywords;
        public UnityShaderStage Vertex;
        public UnityShaderStage Fragment;
        public UnityRenderState State;
        /// <summary>Direct3D 11 (DXBC, translated by vkd3d-shader) or Vulkan (the game's own SPIR-V).</summary>
        public ShaderCompilerPlatform Platform = ShaderCompilerPlatform.D3D11;
        public bool IsVulkan => Platform == ShaderCompilerPlatform.Vulkan;
        /// <summary>OpenGL ES 3 / OpenGL programs: GLSL compiled by glslang, the parameters found by name in the modules.</summary>
        public bool IsGlsl => Platform == ShaderCompilerPlatform.GLES3Plus || Platform == ShaderCompilerPlatform.OpenGLCore;
        public bool IsWebGpu => Platform == ShaderCompilerPlatform.WebGPU;
        /// <summary>The parameters are found by their names in the modules (OpenGL, WebGPU), not by their registers.</summary>
        public bool NamesParameters => IsGlsl || IsWebGpu;

        /// <summary>The set and binding a Vulkan parameter register names.</summary>
        public static (uint set, uint binding) VulkanSlot(int register) => ((uint)(register >> 16) & 0xFF, (uint)register & 0xFFFF);

        private sealed record Candidate(uint BlobIndex, ShaderGpuProgramType Type, string[] Keywords, int Tier, SerializedSubProgram Serialized, uint ParameterIndex);

        private static readonly string[] preferredLightModes = { "FORWARDBASE", "UNIVERSALFORWARD", "UNIVERSALFORWARDONLY", "SRPDEFAULTUNLIT", "ALWAYS", "", "FORWARDONLY", "FORWARD", "VERTEX" };

        //keywords of features the preview doesn't set up: their variants read data that is missing
        private static bool IsUnwantedKeyword(string keyword) =>
            keyword.StartsWith("SHADOWS_", StringComparison.Ordinal) || keyword.StartsWith("LIGHTMAP_", StringComparison.Ordinal) || keyword == "DYNAMICLIGHTMAP_ON"
            || keyword.StartsWith("DIRLIGHTMAP_", StringComparison.Ordinal) || keyword == "INSTANCING_ON" || keyword.StartsWith("STEREO_", StringComparison.Ordinal)
            || keyword == "UNITY_SINGLE_PASS_STEREO" || keyword.StartsWith("FOG_", StringComparison.Ordinal) || keyword.StartsWith("_MAIN_LIGHT_SHADOWS", StringComparison.Ordinal)
            || keyword.StartsWith("_ADDITIONAL_LIGHT", StringComparison.Ordinal) || keyword == "VERTEXLIGHT_ON" || keyword == "LIGHTMAP_SHADOW_MIXING" || keyword == "SHADOWS_SHADOWMASK"
            || keyword == "_SCREEN_SPACE_OCCLUSION" || keyword == "DOTS_INSTANCING_ON" || keyword == "PROCEDURAL_INSTANCING_ON" || keyword == "EDITOR_VISUALIZATION"
            || keyword == "_MIXED_LIGHTING_SUBTRACTIVE" || keyword.StartsWith("_LIGHT_LAYERS", StringComparison.Ordinal) || keyword == "_LIGHT_COOKIES"
            || keyword.StartsWith("_FORWARD_PLUS", StringComparison.Ordinal) || keyword.StartsWith("_REFLECTION_PROBE", StringComparison.Ordinal) || keyword == "LOD_FADE_CROSSFADE"
            || keyword == "_DBUFFER_MRT1" || keyword == "_DBUFFER_MRT2" || keyword == "_DBUFFER_MRT3" || keyword == "DEBUG_DISPLAY" || keyword == "_CLUSTER_LIGHT_LOOP";

        //the keywords of the light the preview sets up
        private static bool IsWantedKeyword(string keyword) => keyword == "DIRECTIONAL" || keyword == "LIGHTPROBE_SH";

        /// <summary>The shadows of the main light the preview can give: Unity's screen space shadows, URP's shadow map (one cascade).</summary>
        public static bool IsShadowKeyword(string keyword) => keyword == "SHADOWS_SCREEN" || keyword == "_MAIN_LIGHT_SHADOWS";

        /// <summary>Whether the variant receives the main light's shadows (see <see cref="IsShadowKeyword"/>).</summary>
        public bool ReceivesShadows => Keywords?.Any(IsShadowKeyword) == true;

        /// <summary>The variant for a material; null with the reason when there is none the preview can draw.</summary>
        public static UnityShaderVariant Select(Shader shader, IReadOnlyCollection<string> materialKeywords, Material material, out string reason, bool shadows = false)
        {
            reason = null;
            var form = shader?.m_ParsedForm;
            if (form == null || shader.compressedBlob == null)
            {
                reason = "the shader has no compiled programs (Unity 5.5 and up)";
                return null;
            }
            //Direct3D 11 (translated) first, then the Vulkan programs as they are, then OpenGL (ES) compiled by glslang, then
            //WebGPU compiled by naga
            var platformId = new[] { ShaderCompilerPlatform.D3D11, ShaderCompilerPlatform.Vulkan, ShaderCompilerPlatform.GLES3Plus, ShaderCompilerPlatform.OpenGLCore, ShaderCompilerPlatform.WebGPU }
                .Where(x => x != ShaderCompilerPlatform.GLES3Plus && x != ShaderCompilerPlatform.OpenGLCore || Glslang.IsAvailable)
                .Where(x => x != ShaderCompilerPlatform.WebGPU || Naga.IsAvailable)
                .FirstOrDefault(x => shader.platforms.Contains(x), ShaderCompilerPlatform.None);
            var platform = Array.IndexOf(shader.platforms, platformId);
            if (platform < 0)
            {
                reason = $"the shader has no Direct3D 11, Vulkan, OpenGL (ES) 3 or WebGPU programs (platforms: {string.Join(", ", shader.platforms)})";
                return null;
            }
            var vulkan = platformId == ShaderCompilerPlatform.Vulkan;
            var glsl = platformId == ShaderCompilerPlatform.GLES3Plus || platformId == ShaderCompilerPlatform.OpenGLCore;
            var webgpu = platformId == ShaderCompilerPlatform.WebGPU;
            string decodeError = null;
            var keywords = new HashSet<string>(materialKeywords ?? Array.Empty<string>(), StringComparer.Ordinal);
            ShaderProgram programs = null;

            foreach (var subShader in form.m_SubShaders)
            {
                var passes = subShader.m_Passes
                    .Select(pass => (pass, lightMode: pass.m_State?.m_Tags?.tags?.FirstOrDefault(t => string.Equals(t.Key, "LightMode", StringComparison.OrdinalIgnoreCase)).Value?.ToUpperInvariant() ?? ""))
                    .Where(x => x.pass.m_Type == PassType.Normal && Array.IndexOf(preferredLightModes, x.lightMode) >= 0)
                    .OrderBy(x => Array.IndexOf(preferredLightModes, x.lightMode));
                foreach (var (pass, lightMode) in passes)
                {
                    var names = pass.m_NameIndices?.GroupBy(x => x.Value).ToDictionary(x => x.Key, x => x.First().Key) ?? new Dictionary<int, string>();
                    string KeywordName(int index) => form.m_KeywordNames != null ? (index < form.m_KeywordNames.Length ? form.m_KeywordNames[index] : null) : names.GetValueOrDefault(index);
                    string[] Names(IEnumerable<ushort> indices) => indices.Select(x => KeywordName(x)).Where(x => x != null).ToArray();
                    double Score(string[] variantKeywords)
                    {
                        double score = 0;
                        foreach (var keyword in variantKeywords)
                        {
                            if (keywords.Contains(keyword))
                                score += 10;
                            else if (IsWantedKeyword(keyword) || shadows && IsShadowKeyword(keyword))
                                score += 1;
                            else if (IsUnwantedKeyword(keyword))
                                score -= 20;
                            else
                                score -= 5;
                        }
                        return score;
                    }
                    //the sub programs of a stage: serialized ones (up to 2021.3.9), or player ones with a parameter entry (2021.3.10 and up)
                    List<Candidate> Candidates(SerializedProgram program)
                    {
                        var list = new List<Candidate>();
                        bool IsDx11(ShaderGpuProgramType type) => webgpu ? type == ShaderGpuProgramType.WGSL : vulkan ? type == ShaderGpuProgramType.SPIRV
                            : glsl ? type >= ShaderGpuProgramType.GLES31AEP && type <= ShaderGpuProgramType.GLES3 || type >= ShaderGpuProgramType.GLCore32 && type <= ShaderGpuProgramType.GLCore43
                            : type >= ShaderGpuProgramType.DX11VertexSM40 && type <= ShaderGpuProgramType.DX11DomainSM50;
                        foreach (var sub in program?.m_SubPrograms ?? new List<SerializedSubProgram>())
                        {
                            if (IsDx11(sub.m_GpuProgramType))
                                list.Add(new Candidate(sub.m_BlobIndex, sub.m_GpuProgramType, Names((sub.m_KeywordIndices ?? Array.Empty<ushort>()).Concat(sub.m_GlobalKeywordIndices ?? Array.Empty<ushort>()).Concat(sub.m_LocalKeywordIndices ?? Array.Empty<ushort>())), sub.m_ShaderHardwareTier, sub, uint.MaxValue));
                        }
                        var players = program?.m_PlayerSubPrograms;
                        for (int tier = 0; players != null && tier < players.Count; tier++)
                        {
                            for (int i = 0; i < players[tier].Count; i++)
                            {
                                var sub = players[tier][i];
                                var parameterIndex = program.m_ParameterBlobIndices != null && tier < program.m_ParameterBlobIndices.Length && i < program.m_ParameterBlobIndices[tier].Length
                                    ? program.m_ParameterBlobIndices[tier][i] : uint.MaxValue;
                                if (IsDx11(sub.m_GpuProgramType))
                                    list.Add(new Candidate(sub.m_BlobIndex, sub.m_GpuProgramType, Names(sub.m_KeywordIndices ?? Array.Empty<ushort>()), tier, null, parameterIndex));
                            }
                        }
                        return list;
                    }
                    Candidate Best(SerializedProgram program, string[] prefer)
                    {
                        var candidates = Candidates(program);
                        if (candidates.Count == 0)
                            return null;
                        //the same keywords as the other stage first (the variants of both stages match), then the best score
                        return candidates
                            .OrderByDescending(x => prefer != null && x.Keywords.OrderBy(k => k).SequenceEqual(prefer.OrderBy(k => k)) ? 1 : 0)
                            .ThenByDescending(x => Score(x.Keywords))
                            .ThenBy(x => x.Tier)
                            .First();
                    }
                    //Vulkan and OpenGL programs hold every stage: the vertex program's when the fragment has none of its own
                    var fragmentSub = Best(pass.progFragment, null) ?? (vulkan || glsl || webgpu ? Best(pass.progVertex, null) : null);
                    if (fragmentSub == null)
                        continue;
                    var fragmentKeywords = fragmentSub.Keywords;
                    var vertexSub = Best(pass.progVertex, fragmentKeywords);
                    if (vertexSub == null)
                        continue;

                    programs ??= ShaderConverter.ReadShaderProgram(shader, platform);
                    UnityShaderStage Stage(Candidate sub, SerializedProgram program, int stageIndex)
                    {
                        if (sub.BlobIndex >= programs.m_SubPrograms.Length || programs.m_SubPrograms[sub.BlobIndex] == null)
                            return null;
                        var blob = programs.m_SubPrograms[sub.BlobIndex];
                        var code = blob.m_ProgramCode;
                        UnityShaderStage stage;
                        if (vulkan)
                        {
                            byte[] spirv;
                            try
                            {
                                spirv = SpirVShaderConverter.DecodeStages(code)[stageIndex];
                                if (spirv == null && stageIndex == 1 && vertexSub.BlobIndex < programs.m_SubPrograms.Length)
                                    spirv = SpirVShaderConverter.DecodeStages(programs.m_SubPrograms[vertexSub.BlobIndex]?.m_ProgramCode)[1];
                            }
                            catch (Exception e)
                            {
                                decodeError = $"unable to decode the Vulkan program: {e.Message}";
                                return null;
                            }
                            if (spirv == null)
                                return null;
                            stage = new UnityShaderStage { ProgramType = sub.Type, Spirv = spirv };
                            //the bind channels: after the code (2021.3.10 and up) or serialized; the target of an input is 13 + its location
                            List<(uint source, uint target)> channels = blob.BindChannels.Count > 0 ? blob.BindChannels
                                : sub.Serialized?.m_Channels?.m_Channels.Select(x => ((uint)x.source, (uint)x.target)).ToList() ?? new List<(uint, uint)>();
                            stage.Inputs.AddRange(channels.Where(x => x.target >= 13).Select(x => ((int)x.source, (int)x.target - 13)));
                        }
                        else if (webgpu)
                        {
                            //the stage's WGSL, from the vertex program when the fragment one has none
                            var source = stageIndex == 0 ? UnityWgsl.Stages(code).Vertex : UnityWgsl.Stages(code).Fragment;
                            if (source == null && stageIndex == 1 && vertexSub.BlobIndex < programs.m_SubPrograms.Length && programs.m_SubPrograms[vertexSub.BlobIndex] != null)
                                source = UnityWgsl.Stages(programs.m_SubPrograms[vertexSub.BlobIndex].m_ProgramCode).Fragment;
                            if (source == null)
                                return null;
                            stage = new UnityShaderStage { ProgramType = sub.Type, Wgsl = source };
                        }
                        else if (glsl)
                        {
                            //the #ifdef VERTEX / FRAGMENT part of the program's GLSL, the fragment one from the vertex program when missing
                            var name = stageIndex == 0 ? "VERTEX" : "FRAGMENT";
                            var source = UnityGlsl.Stage(Encoding.UTF8.GetString(code), name);
                            if (source == null && stageIndex == 1 && vertexSub.BlobIndex < programs.m_SubPrograms.Length && programs.m_SubPrograms[vertexSub.BlobIndex] != null)
                                source = UnityGlsl.Stage(Encoding.UTF8.GetString(programs.m_SubPrograms[vertexSub.BlobIndex].m_ProgramCode), name);
                            if (source == null)
                            {
                                decodeError = "no OpenGL ES 3 / OpenGL program source (GLSL ES 1.0 is not supported)";
                                return null;
                            }
                            stage = new UnityShaderStage { ProgramType = sub.Type, Glsl = source };
                        }
                        else
                        {
                            var start = FindDxbc(code);
                            if (start < 0)
                                return null;
                            stage = new UnityShaderStage { ProgramType = sub.Type, Dxbc = code.AsSpan(start).ToArray() };
                        }
                        if (sub.ParameterIndex != uint.MaxValue)
                        {
                            //2021.3.10 and up: the layouts of the constant buffers are shared by the program, the bindings are the variant's
                            var entry = programs.GetParameterEntry(sub.ParameterIndex);
                            var common = program.m_CommonParameters;
                            var bindings = (entry?.ConstantBufferBindings ?? new List<(string, int)>())
                                .Concat(common?.m_ConstantBufferBindings.Select(x => (names.GetValueOrDefault(x.m_NameIndex), x.m_Index)) ?? Enumerable.Empty<(string, int)>())
                                .Where(x => x.Item1 != null).GroupBy(x => x.Item2).Select(x => x.First());
                            foreach (var (name, register) in bindings)
                            {
                                var layout = common?.m_ConstantBuffers.FirstOrDefault(x => names.GetValueOrDefault(x.m_NameIndex) == name);
                                var cb = new UnityConstantBuffer { Name = name, Register = register, Size = Math.Max(layout?.m_Size ?? 0, entry?.ConstantBuffers.FirstOrDefault(x => x.Name == name)?.Size ?? 0) };
                                if (layout != null)
                                {
                                    foreach (var vector in layout.m_VectorParams)
                                        cb.Fields.Add(new UnityShaderField { Name = names.GetValueOrDefault(vector.m_NameIndex), Offset = vector.m_Index, ArraySize = vector.m_ArraySize, Components = vector.m_Dim });
                                    foreach (var matrix in layout.m_MatrixParams)
                                        cb.Fields.Add(new UnityShaderField { Name = names.GetValueOrDefault(matrix.m_NameIndex), Offset = matrix.m_Index, ArraySize = matrix.m_ArraySize, IsMatrix = true, Components = matrix.m_RowCount });
                                }
                                cb.Fields.AddRange(entry?.ConstantBuffers.FirstOrDefault(x => x.Name == name)?.Fields ?? new List<UnityShaderField>());
                                stage.ConstantBuffers.Add(cb);
                            }
                            stage.Textures.AddRange(entry?.Textures ?? new List<UnityShaderTexture>());
                            foreach (var texture in common?.m_TextureParams ?? new List<TextureParameter>())
                            {
                                if (!stage.Textures.Any(x => x.Register == texture.m_Index))
                                    stage.Textures.Add(new UnityShaderTexture { Name = names.GetValueOrDefault(texture.m_NameIndex), Register = texture.m_Index, SamplerRegister = texture.m_SamplerIndex, Dimension = texture.m_Dim });
                            }
                            return stage;
                        }
                        //the blob has the parameters with their names (5.5 and up); the serialized ones otherwise
                        var blobParameters = programs.m_SubPrograms[sub.BlobIndex].Parameters;
                        if (blobParameters != null && (blobParameters.ConstantBuffers.Count > 0 || blobParameters.Textures.Count > 0 || !HasSerializedParameters(sub.Serialized, program)))
                        {
                            stage.ConstantBuffers.AddRange(blobParameters.ConstantBuffers.Where(x => x.Register >= 0));
                            stage.Textures.AddRange(blobParameters.Textures);
                        }
                        else
                        {
                            ReadParameters(stage, sub.Serialized, program.m_CommonParameters, names);
                        }
                        return stage;
                    }
                    var vertex = Stage(vertexSub, pass.progVertex, 0);
                    var fragment = Stage(fragmentSub, pass.progFragment, 1);
                    if (vertex == null || fragment == null)
                        continue;
                    if (vulkan)
                    {
                        //the parameters of a Vulkan program are those of every stage (their registers say which)
                        foreach (var (a, b) in new[] { (vertex, fragment), (fragment, vertex) })
                        {
                            a.ConstantBuffers.AddRange(b.ConstantBuffers.Where(x => !a.ConstantBuffers.Any(y => y.Register == x.Register)).ToList());
                            a.Textures.AddRange(b.Textures.Where(x => !a.Textures.Any(y => y.Register == x.Register)).ToList());
                        }
                    }
                    return new UnityShaderVariant
                    {
                        ShaderName = form.m_Name,
                        PassName = pass.m_State?.m_Name,
                        LightMode = lightMode,
                        Keywords = fragmentKeywords.Union(vertexSub.Keywords).ToArray(),
                        Vertex = vertex,
                        Fragment = fragment,
                        State = ReadState(pass.m_State, material),
                        Platform = platformId,
                    };
                }
            }
            reason ??= decodeError ?? (vulkan ? "no forward pass with Vulkan programs" : glsl ? "no forward pass with OpenGL (ES) programs"
                : webgpu ? "no forward pass with WebGPU programs" : "no forward pass with Direct3D 11 vertex and fragment programs");
            return null;
        }

        private static bool HasSerializedParameters(SerializedSubProgram sub, SerializedProgram program) =>
            sub != null && sub.m_Parameters?.m_ConstantBufferBindings.Count > 0 || sub.m_ConstantBufferBindings?.Count > 0 || program.m_CommonParameters?.m_ConstantBufferBindings.Count > 0;

        private static int FindDxbc(byte[] code)
        {
            for (int i = 0; i + 4 <= Math.Min(code.Length, 128); i++)
            {
                if (code[i] == 'D' && code[i + 1] == 'X' && code[i + 2] == 'B' && code[i + 3] == 'C')
                    return i;
            }
            return -1;
        }

        private static void ReadParameters(UnityShaderStage stage, SerializedSubProgram sub, SerializedProgramParameters common, Dictionary<int, string> names)
        {
            //2020.3.2 and up: the parameters shared by the sub programs are in the program, each sub program has its own too
            var parameters = sub.m_Parameters;
            var constantBuffers = (parameters?.m_ConstantBuffers ?? sub.m_ConstantBuffers ?? new List<ConstantBuffer>()).Concat(common?.m_ConstantBuffers ?? new List<ConstantBuffer>()).ToList();
            var bindings = (parameters?.m_ConstantBufferBindings ?? sub.m_ConstantBufferBindings ?? new List<BufferBinding>()).Concat(common?.m_ConstantBufferBindings ?? new List<BufferBinding>())
                .GroupBy(x => x.m_Index).Select(x => x.First()).ToList();
            var textures = (parameters?.m_TextureParams ?? sub.m_TextureParams ?? new List<TextureParameter>()).Concat(common?.m_TextureParams ?? new List<TextureParameter>())
                .GroupBy(x => x.m_Index).Select(x => x.First()).ToList();
            foreach (var binding in bindings)
            {
                var name = names.GetValueOrDefault(binding.m_NameIndex);
                var buffer = constantBuffers.FirstOrDefault(x => x.m_NameIndex == binding.m_NameIndex);
                var cb = new UnityConstantBuffer { Name = name, Register = binding.m_Index, Size = buffer?.m_Size ?? 0 };
                if (buffer != null)
                {
                    foreach (var vector in buffer.m_VectorParams)
                        cb.Fields.Add(new UnityShaderField { Name = names.GetValueOrDefault(vector.m_NameIndex), Offset = vector.m_Index, ArraySize = vector.m_ArraySize, Components = vector.m_Dim });
                    foreach (var matrix in buffer.m_MatrixParams)
                        cb.Fields.Add(new UnityShaderField { Name = names.GetValueOrDefault(matrix.m_NameIndex), Offset = matrix.m_Index, ArraySize = matrix.m_ArraySize, IsMatrix = true, Components = matrix.m_RowCount });
                }
                stage.ConstantBuffers.Add(cb);
            }
            foreach (var texture in textures)
            {
                stage.Textures.Add(new UnityShaderTexture { Name = names.GetValueOrDefault(texture.m_NameIndex), Register = texture.m_Index, SamplerRegister = texture.m_SamplerIndex, Dimension = texture.m_Dim });
            }
        }

        private static UnityRenderState ReadState(SerializedShaderState state, Material material)
        {
            var result = new UnityRenderState();
            if (state == null)
                return result;
            float Value(SerializedShaderFloatValue value, float fallback)
            {
                if (value == null)
                    return fallback;
                if (!string.IsNullOrEmpty(value.name) && material != null)
                {
                    foreach (var (key, v) in material.m_SavedProperties.m_Floats)
                    {
                        if (key == value.name)
                            return v;
                    }
                }
                return value.val;
            }
            result.Cull = (int)Value(state.culling, 2);
            result.ZWrite = Value(state.zWrite, 1) != 0;
            result.ZTest = (int)Value(state.zTest, 4);
            var blend = state.rtBlend?.FirstOrDefault();
            if (blend != null)
            {
                result.SrcBlend = (int)Value(blend.srcBlend, 1);
                result.DstBlend = (int)Value(blend.destBlend, 0);
                result.SrcBlendAlpha = (int)Value(blend.srcBlendAlpha, 1);
                result.DstBlendAlpha = (int)Value(blend.destBlendAlpha, 0);
                result.BlendOp = (int)Value(blend.blendOp, 0);
                result.BlendOpAlpha = (int)Value(blend.blendOpAlpha, 0);
                result.ColorMask = (int)Value(blend.colMask, 15);
                result.Blend = !(result.SrcBlend == 1 && result.DstBlend == 0 && result.SrcBlendAlpha == 1 && result.DstBlendAlpha == 0);
            }
            return result;
        }
    }

    /// <summary>The input signature (ISGN) of a DXBC program: semantic, index and input register.</summary>
    public static class DxbcSignature
    {
        public readonly record struct Element(string Semantic, int SemanticIndex, int Register, int SystemValue, int ComponentType, int Mask);

        public static List<Element> ReadInputs(byte[] dxbc)
        {
            var result = new List<Element>();
            if (dxbc.Length < 32)
                return result;
            var chunkCount = BitConverter.ToInt32(dxbc, 28);
            for (int c = 0; c < chunkCount; c++)
            {
                var offset = BitConverter.ToInt32(dxbc, 32 + c * 4);
                var fourCC = Encoding.ASCII.GetString(dxbc, offset, 4);
                if (fourCC != "ISGN" && fourCC != "ISG1")
                    continue;
                var data = offset + 8;
                var count = BitConverter.ToInt32(dxbc, data);
                var elementSize = fourCC == "ISG1" ? 32 : 24;
                var first = data + (fourCC == "ISG1" ? 8 : 8);
                for (int i = 0; i < count; i++)
                {
                    var e = first + i * elementSize;
                    if (fourCC == "ISG1")
                        e += 4; //stream
                    var nameOffset = BitConverter.ToInt32(dxbc, e);
                    var end = Array.IndexOf(dxbc, (byte)0, data + nameOffset);
                    var name = Encoding.ASCII.GetString(dxbc, data + nameOffset, end - data - nameOffset);
                    result.Add(new Element(name, BitConverter.ToInt32(dxbc, e + 4), BitConverter.ToInt32(dxbc, e + 16), BitConverter.ToInt32(dxbc, e + 8),
                        BitConverter.ToInt32(dxbc, e + 12), dxbc[e + 20]));
                }
                break;
            }
            return result;
        }

        /// <summary>The registers of the comparison samplers (dcl_sampler s#, mode_comparison) of a shader model 4/5 program.</summary>
        public static HashSet<int> ComparisonSamplers(byte[] dxbc)
        {
            var result = new HashSet<int>();
            if (dxbc == null || dxbc.Length < 32)
                return result;
            var chunkCount = BitConverter.ToInt32(dxbc, 28);
            for (int c = 0; c < chunkCount; c++)
            {
                var offset = BitConverter.ToInt32(dxbc, 32 + c * 4);
                var fourCC = Encoding.ASCII.GetString(dxbc, offset, 4);
                if (fourCC != "SHDR" && fourCC != "SHEX")
                    continue;
                var start = offset + 8;
                var end = Math.Min(dxbc.Length, start + BitConverter.ToInt32(dxbc, start + 4) * 4);
                for (int i = start + 8; i + 4 <= end;)
                {
                    var token = BitConverter.ToUInt32(dxbc, i);
                    var opcode = token & 0x7FF;
                    var length = opcode == 0x35 ? BitConverter.ToInt32(dxbc, i + 4) : (int)((token >> 24) & 0x7F); //customdata: its own length
                    if (length <= 0)
                        break;
                    if (opcode == 0x5A && ((token >> 11) & 0xF) == 1 && i + 12 <= end) //dcl_sampler, mode_comparison
                        result.Add(BitConverter.ToInt32(dxbc, i + 8));
                    i += length * 4;
                }
                break;
            }
            return result;
        }
    }

    /// <summary>
    /// What the preview needs from a SPIR-V module written by vkd3d-shader: its descriptors (set, binding, D3D register from
    /// the name vkd3d gives them: cb&lt;register&gt;_&lt;n&gt;, t&lt;register&gt;, s&lt;register&gt;), the size of the constant
    /// buffers and the locations of the inputs.
    /// </summary>
    public sealed class SpirvReflection
    {
        public enum ResourceKind { UniformBuffer, Image, Sampler, CombinedImageSampler }

        public sealed class Resource
        {
            public ResourceKind Kind;
            public string Name;
            public int Register = -1;
            public uint Set;
            public uint Binding;
            public int Size; //uniform buffers
            public int ImageDimension; //SPIR-V Dim: 0 1D, 1 2D, 2 3D, 3 cube
            public bool Arrayed;
            /// <summary>Uniform buffers: the members by name (HLSLcc's hlslcc_mtx4x4 vec4 arrays as matrices).</summary>
            public List<UnityShaderField> Fields = new List<UnityShaderField>();
        }

        public List<Resource> Resources { get; } = new List<Resource>();
        public List<int> InputLocations { get; } = new List<int>();
        /// <summary>The names of the inputs by location (GLSL: in_POSITION0, in_TEXCOORD1...).</summary>
        public Dictionary<int, string> InputNames { get; } = new Dictionary<int, string>();
        /// <summary>The module reads storage buffers (structured buffers, e.g. VFX Graph's particles): not for the preview.</summary>
        public bool UsesStorageBuffers { get; private set; }

        public static SpirvReflection Read(byte[] spirv)
        {
            var words = new uint[spirv.Length / 4];
            Buffer.BlockCopy(spirv, 0, words, 0, words.Length * 4);
            var names = new Dictionary<uint, string>();
            var decorations = new Dictionary<(uint id, uint decoration), uint>();
            var types = new Dictionary<uint, (uint op, uint[] operands)>();
            var constants = new Dictionary<uint, uint>();
            var variables = new List<(uint type, uint id, uint storage)>();
            var memberOffsets = new Dictionary<(uint type, uint member), uint>();
            var memberNames = new Dictionary<(uint type, uint member), string>();
            for (int i = 5; i < words.Length;)
            {
                var op = words[i] & 0xFFFF;
                var count = (int)(words[i] >> 16);
                if (count == 0)
                    break;
                switch (op)
                {
                    case 5: //OpName
                        names[words[i + 1]] = ReadString(words, i + 2);
                        break;
                    case 6: //OpMemberName
                        memberNames[(words[i + 1], words[i + 2])] = ReadString(words, i + 3);
                        break;
                    case 71: //OpDecorate
                        decorations[(words[i + 1], words[i + 2])] = count > 3 ? words[i + 3] : 1;
                        break;
                    case 72: //OpMemberDecorate
                        if (words[i + 3] == 35) //Offset
                            memberOffsets[(words[i + 1], words[i + 2])] = words[i + 4];
                        break;
                    case 43: //OpConstant
                        constants[words[i + 2]] = words[i + 3];
                        break;
                    case 59: //OpVariable
                        variables.Add((words[i + 1], words[i + 2], words[i + 3]));
                        break;
                    default:
                        if (op >= 19 && op <= 39) //type declarations
                            types[words[i + 1]] = (op, words.AsSpan(i + 2, count - 2).ToArray());
                        break;
                }
                i += count;
            }

            int SizeOf(uint type)
            {
                if (!types.TryGetValue(type, out var t))
                    return 0;
                switch (t.op)
                {
                    case 21: //int
                    case 22: //float
                        return (int)t.operands[0] / 8;
                    case 23: //vector
                        return SizeOf(t.operands[0]) * (int)t.operands[1];
                    case 24: //matrix
                        return SizeOf(t.operands[0]) * (int)t.operands[1];
                    case 28: //array
                        return SizeOf(t.operands[0]) * (int)constants.GetValueOrDefault(t.operands[1]);
                    case 30: //struct
                        {
                            var size = 0;
                            for (uint m = 0; m < t.operands.Length; m++)
                                size = Math.Max(size, (int)memberOffsets.GetValueOrDefault((type, m)) + SizeOf(t.operands[m]));
                            return size;
                        }
                    default:
                        return 0;
                }
            }

            //the members of a block, the members of nested structs flattened (naga wraps a uniform struct in a block of one member)
            void AddFields(uint structType, int baseOffset, List<UnityShaderField> fields)
            {
                if (!types.TryGetValue(structType, out var st) || st.op != 30)
                    return;
                for (uint m = 0; m < st.operands.Length; m++)
                {
                    if (!memberOffsets.TryGetValue((structType, m), out var memberOffset))
                        continue;
                    var offset = baseOffset + (int)memberOffset;
                    var memberType = st.operands[m];
                    if (types.TryGetValue(memberType, out var nested) && nested.op == 30)
                    {
                        AddFields(memberType, offset, fields);
                        continue;
                    }
                    if (!memberNames.TryGetValue((structType, m), out var memberName))
                        continue;
                    //the element type and count of arrays, the components of vectors
                    var arraySize = 0;
                    if (types.TryGetValue(memberType, out var t) && t.op == 28)
                    {
                        arraySize = (int)constants.GetValueOrDefault(t.operands[1]);
                        memberType = t.operands[0];
                    }
                    var components = types.TryGetValue(memberType, out var element) && element.op == 23 ? (int)element.operands[1] : 1;
                    var field = new UnityShaderField { Name = memberName, Offset = offset, ArraySize = arraySize, Components = components };
                    if (memberName.StartsWith("hlslcc_mtx4x4", StringComparison.Ordinal))
                    {
                        //4 columns per matrix
                        field.Name = memberName.Substring("hlslcc_mtx4x4".Length);
                        field.IsMatrix = true;
                        field.Components = 4;
                        field.ArraySize = arraySize > 4 ? arraySize / 4 : 0;
                    }
                    fields.Add(field);
                }
            }

            var reflection = new SpirvReflection();
            foreach (var (pointerType, id, storage) in variables)
            {
                if (!types.TryGetValue(pointerType, out var pointer) || pointer.op != 32) //OpTypePointer
                    continue;
                var type = pointer.operands[1];
                if (storage == 1) //Input
                {
                    if (!decorations.ContainsKey((id, 11)) && decorations.TryGetValue((id, 30), out var location)) //not BuiltIn; Location
                    {
                        reflection.InputLocations.Add((int)location);
                        if (names.TryGetValue(id, out var inputName))
                            reflection.InputNames[(int)location] = inputName;
                    }
                    continue;
                }
                if (storage == 12 || storage == 2 && decorations.ContainsKey((type, 3u))) //StorageBuffer, Uniform with BufferBlock
                {
                    reflection.UsesStorageBuffers = true;
                    continue;
                }
                if (storage != 0 && storage != 2) //UniformConstant, Uniform
                    continue;
                if (!decorations.TryGetValue((id, 33), out var binding))
                    continue;
                var resource = new Resource
                {
                    Name = names.GetValueOrDefault(id) ?? "",
                    Set = decorations.GetValueOrDefault((id, 34u)),
                    Binding = binding,
                };
                types.TryGetValue(type, out var resourceType);
                switch (resourceType.op)
                {
                    case 30: //struct: a uniform buffer
                        resource.Kind = ResourceKind.UniformBuffer;
                        resource.Size = SizeOf(type);
                        AddFields(type, 0, resource.Fields);
                        break;
                    case 25: //image
                        resource.Kind = ResourceKind.Image;
                        resource.ImageDimension = (int)resourceType.operands[1];
                        resource.Arrayed = resourceType.operands[3] != 0;
                        break;
                    case 26: //sampler
                        resource.Kind = ResourceKind.Sampler;
                        break;
                    case 27: //sampled image
                        resource.Kind = ResourceKind.CombinedImageSampler;
                        if (types.TryGetValue(resourceType.operands[0], out var image))
                        {
                            resource.ImageDimension = (int)image.operands[1];
                            resource.Arrayed = image.operands[3] != 0;
                        }
                        break;
                    default:
                        continue;
                }
                resource.Register = ParseRegister(resource.Name);
                reflection.Resources.Add(resource);
            }
            return reflection;
        }

        private static string ReadString(uint[] words, int start)
        {
            var bytes = new List<byte>();
            for (int i = start; i < words.Length; i++)
            {
                for (int b = 0; b < 4; b++)
                {
                    var c = (byte)(words[i] >> (8 * b));
                    if (c == 0)
                        return Encoding.UTF8.GetString(bytes.ToArray());
                    bytes.Add(c);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        //cb3_0 -> 3, t2 -> 2, s0 -> 0
        private static int ParseRegister(string name)
        {
            var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var register) ? register : -1;
        }

        /// <summary>A copy of the module with every descriptor moved to a descriptor set (vkd3d puts both stages in set 0).</summary>
        public static byte[] WithDescriptorSet(byte[] spirv, uint set)
        {
            var result = (byte[])spirv.Clone();
            for (int i = 20; i + 4 <= result.Length;)
            {
                var word = BitConverter.ToUInt32(result, i);
                var op = word & 0xFFFF;
                var count = (int)(word >> 16);
                if (count == 0)
                    break;
                if (op == 71 && count == 4 && BitConverter.ToUInt32(result, i + 8) == 34) //OpDecorate DescriptorSet
                    BitConverter.TryWriteBytes(result.AsSpan(i + 12), set);
                i += count * 4;
            }
            return result;
        }
    }

    /// <summary>
    /// The GLSL of Unity's OpenGL (ES) programs for glslang's Vulkan rules: the #ifdef VERTEX and #ifdef FRAGMENT parts of
    /// a program, ES 3.0 as 3.1 (Vulkan needs 3.1), the explicit uniform locations and bindings left to glslang.
    /// </summary>
    public static class UnityGlsl
    {
        /// <summary>The lines of the #ifdef &lt;stage&gt; ... #endif part of a program, null when it has none.</summary>
        public static List<string> Section(string program, string stage)
        {
            var lines = program.Replace("\r\n", "\n").Split('\n');
            var start = Array.FindIndex(lines, x => x.Trim() == "#ifdef " + stage);
            if (start < 0)
                return null;
            var depth = 0;
            for (int i = start; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("#if", StringComparison.Ordinal))
                    depth++;
                else if (line.StartsWith("#endif", StringComparison.Ordinal) && --depth == 0)
                    return lines.Skip(start + 1).Take(i - start - 1).ToList();
            }
            return null;
        }

        /// <summary>The source of a stage ("VERTEX" or "FRAGMENT"), null when the program has none or is GLSL ES 1.0.</summary>
        public static string Stage(string program, string stage)
        {
            var body = Section(program, stage);
            if (body == null)
                return null;
            var version = body.FindIndex(x => x.TrimStart().StartsWith("#version", StringComparison.Ordinal));
            if (version < 0)
                return null;
            var directive = body[version].Trim();
            if (directive == "#version 100")
                return null; //GLSL ES 1.0 (attribute, varying, gl_FragColor): not for Vulkan
            if (directive == "#version 300 es")
                body[version] = "#version 310 es";
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i].Trim() == "#define UNITY_SUPPORTS_UNIFORM_LOCATION 1")
                    body[i] = "#define UNITY_SUPPORTS_UNIFORM_LOCATION 0";
            }
            //the #version first
            var ordered = new List<string> { body[version] };
            ordered.AddRange(body.Where((_, i) => i != version));
            return string.Join("\n", ordered) + "\n";
        }
    }

    /// <summary>
    /// The WGSL of Unity's WebGPU programs: a header of (offset, length) per stage (vertex, fragment) and a flags word,
    /// then the sources (6000.3 and up); or the #ifdef VERTEX / FRAGMENT parts of one text (6000.0).
    /// </summary>
    public static class UnityWgsl
    {
        public static (string Vertex, string Fragment) Stages(byte[] code)
        {
            var headerSize = code.Length >= 4 ? BitConverter.ToInt32(code, 0) : 0;
            if (headerSize >= 12 && headerSize <= code.Length && (headerSize - 4) % 8 == 0)
            {
                string Part(int i)
                {
                    if (i * 8 + 8 > headerSize - 4)
                        return null;
                    var offset = BitConverter.ToInt32(code, i * 8);
                    var length = BitConverter.ToInt32(code, i * 8 + 4);
                    return length > 0 && offset >= headerSize && offset + length <= code.Length ? Encoding.UTF8.GetString(code, offset, length).TrimEnd('\0') : null;
                }
                return (Part(0), Part(1));
            }
            var text = Encoding.UTF8.GetString(code);
            string Joined(List<string> lines) => lines == null ? null : string.Join("\n", lines) + "\n";
            return (Joined(UnityGlsl.Section(text, "VERTEX")), Joined(UnityGlsl.Section(text, "FRAGMENT")));
        }

        /// <summary>Unity's name of a WGSL identifier: Tint writes the names starting with "_" with an "x" before.</summary>
        public static string UnityName(string name) => name != null && name.StartsWith("x_", StringComparison.Ordinal) ? name.Substring(1) : name;
    }

    /// <summary>
    /// The color space of the project a shader was built for (Linear or Gamma in the Player settings): Unity compiles it into
    /// the programs, and a linear project samples its sRGB textures as such and writes to an sRGB target.
    /// </summary>
    public static class UnityColorSpace
    {
        /// <summary>
        /// Whether a variant was built for a linear project: the dielectric constants of the built-in shaders
        /// (unity_ColorSpaceDielectricSpec, compiled into the programs), else the Player settings loaded with the files
        /// (of the same Unity version), else linear for the scriptable pipelines.
        /// </summary>
        public static bool IsLinear(UnityShaderVariant variant, Material material, out string source)
        {
            var programs = FromPrograms(variant);
            if (programs != null)
            {
                source = "shader constants";
                return programs.Value;
            }
            var settings = FromPlayerSettings(material?.assetsFile?.assetsManager, material?.assetsFile?.unityVersion);
            if (settings != null)
            {
                source = "Player settings";
                return settings.Value;
            }
            source = "default";
            return IsScriptablePipeline(variant);
        }

        /// <summary>
        /// m_ActiveColorSpace (0 gamma, 1 linear) of the PlayerSettings loaded with the files (of that Unity version when
        /// given: files of several games can be loaded together); null when there are none.
        /// </summary>
        public static bool? FromPlayerSettings(AssetsManager manager, string unityVersion = null)
        {
            foreach (var file in manager?.assetsFileList.ToList() ?? new List<SerializedFile>())
            {
                if (unityVersion != null && file.unityVersion != unityVersion)
                    continue;
                foreach (var settings in file.Objects.OfType<PlayerSettings>())
                {
                    try
                    {
                        if (settings.ToType() is { } type && type.Contains("m_ActiveColorSpace"))
                            return Convert.ToInt32(type["m_ActiveColorSpace"]) == 1;
                    }
                    catch (Exception e)
                    {
                        Logger.Verbose($"PlayerSettings of {file.fileName}: {e.Message}");
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// unity_ColorSpaceDielectricSpec is a constant of UnityCG: 0.2209163 (1 - it) in gamma projects, 0.04 (and 0.96) in
        /// linear ones. Only the built-in pipeline's shaders use it; null when the programs don't tell.
        /// </summary>
        public static bool? FromPrograms(UnityShaderVariant variant)
        {
            if (variant?.Fragment?.Wgsl is { } wgsl && !IsScriptablePipeline(variant))
            {
                //Tint prints the constants in full: 0.22091630101203918457f, 0.95999997854232788086f
                if (wgsl.Contains("0.2209163", StringComparison.Ordinal))
                    return false;
                if (wgsl.Contains("0.9599999", StringComparison.Ordinal))
                    return true;
                return null;
            }
            if (variant?.Fragment?.Glsl is { } glsl && !IsScriptablePipeline(variant))
            {
                //the constants as HLSLcc prints them
                if (glsl.Contains("0.220916301", StringComparison.Ordinal) || glsl.Contains("0.779083729", StringComparison.Ordinal))
                    return false;
                if (glsl.Contains("0.959999979", StringComparison.Ordinal))
                    return true;
                return null;
            }
            var code = variant?.Fragment?.Dxbc ?? variant?.Fragment?.Spirv; //immediate constants in both
            if (code == null || IsScriptablePipeline(variant))
                return null;
            if (HasFloat(code, 0.220916301f) && HasFloat(code, 1 - 0.220916301f))
                return false;
            if (HasFloat(code, 0.04f) && HasFloat(code, 0.96f))
                return true;
            return null;
        }

        private static bool IsScriptablePipeline(UnityShaderVariant variant)
        {
            var lightMode = variant?.LightMode?.ToUpperInvariant() ?? "";
            var name = variant?.ShaderName ?? "";
            return lightMode.StartsWith("UNIVERSAL", StringComparison.Ordinal) || lightMode == "SRPDEFAULTUNLIT" || lightMode == "FORWARDONLY"
                || name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal) || name.StartsWith("HDRP/", StringComparison.Ordinal)
                || name.StartsWith("Shader Graphs/", StringComparison.Ordinal);
        }

        private static bool HasFloat(byte[] code, float value)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            for (int i = 0; i + 4 <= code.Length; i += 4)
            {
                if (BitConverter.ToInt32(code, i) == bits)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The values Unity gives its shaders (camera, object, lighting, time) and the material's properties, and the writing of
    /// constant buffers from them. Matrices are in Unity's convention (column vectors, left-handed world, reversed Z for
    /// Direct3D 11) and stored as System.Numerics matrices of the transposed matrix, whose memory is Unity's column-major layout.
    /// </summary>
    public sealed class UnityShaderValues
    {
        private readonly Dictionary<string, float[]> values = new Dictionary<string, float[]>(StringComparer.Ordinal);

        public void Set(string name, params float[] value) => values[name] = value;
        public void Set(string name, Matrix transposed) => values[name] = new[]
        {
            transposed.M11, transposed.M12, transposed.M13, transposed.M14, transposed.M21, transposed.M22, transposed.M23, transposed.M24,
            transposed.M31, transposed.M32, transposed.M33, transposed.M34, transposed.M41, transposed.M42, transposed.M43, transposed.M44,
        };

        public bool TryGet(string name, out float[] value) => values.TryGetValue(name, out value);

        /// <summary>The defaults of the built-in values, for a scene lit by one white directional light and a grey ambient.</summary>
        public static UnityShaderValues CreateDefaults(bool linearColorSpace)
        {
            var v = new UnityShaderValues();
            v.Set("_Time", 0.5f, 10f, 20f, 30f);
            v.Set("_SinTime", 0.12f, 0.24f, 0.47f, 0.84f);
            v.Set("_CosTime", 0.99f, 0.97f, 0.88f, 0.54f);
            v.Set("unity_DeltaTime", 0.016f, 60f, 0.016f, 60f);
            v.Set("_TimeParameters", 10f, MathF.Sin(10f), MathF.Cos(10f), 0f);
            v.Set("unity_WorldTransformParams", 0, 0, 0, 1);
            v.Set("unity_LODFade", 1, 1, 0, 0);
            v.Set("unity_RenderingLayer", 1, 0, 0, 0);
            v.Set("unity_ProbesOcclusion", 1, 1, 1, 1);
            v.Set("unity_OcclusionMaskSelector", 1, 0, 0, 0); //no shadow mask: the (full) occlusion of the first channel
            v.Set("unity_SpecCube0_HDR", 1, 1, 0, 0);
            v.Set("unity_SpecCube1_HDR", 1, 1, 0, 0);
            v.Set("unity_SpecCube0_ProbePosition", 0, 0, 0, 0);
            v.Set("unity_SpecCube0_BoxMax", 1e5f, 1e5f, 1e5f, 1);
            v.Set("unity_SpecCube0_BoxMin", -1e5f, -1e5f, -1e5f, 1);
            v.Set("_GlossyEnvironmentCubeMap_HDR", 1, 1, 0, 0);
            //a soft grey sky: the constant term (SHAr.w...) of the probe
            var ambient = linearColorSpace ? 0.15f : 0.28f;
            v.Set("unity_SHAr", 0, 0.05f, 0, ambient);
            v.Set("unity_SHAg", 0, 0.05f, 0, ambient);
            v.Set("unity_SHAb", 0, 0.08f, 0, ambient * 1.1f);
            v.Set("unity_SHBr", 0, 0, 0, 0);
            v.Set("unity_SHBg", 0, 0, 0, 0);
            v.Set("unity_SHBb", 0, 0, 0, 0);
            v.Set("unity_SHC", 0, 0, 0, 0);
            v.Set("unity_AmbientSky", ambient, ambient, ambient * 1.1f, 1);
            v.Set("unity_AmbientEquator", ambient, ambient, ambient, 1);
            v.Set("unity_AmbientGround", ambient * 0.6f, ambient * 0.6f, ambient * 0.6f, 1);
            v.Set("glstate_lightmodel_ambient", ambient * 0.5f, ambient * 0.5f, ambient * 0.5f, 1);
            v.Set("unity_IndirectSpecColor", ambient, ambient, ambient, 1);
            v.Set("_GlossyEnvironmentColor", ambient, ambient, ambient, 1);
            v.Set("_SubtractiveShadowColor", 0.5f, 0.5f, 0.5f, 1);
            //unity_ColorSpace* of UnityCG.cginc
            if (linearColorSpace)
            {
                v.Set("unity_ColorSpaceGrey", 0.214041144f, 0.214041144f, 0.214041144f, 0.5f);
                v.Set("unity_ColorSpaceDouble", 4.59479380f, 4.59479380f, 4.59479380f, 2.0f);
                v.Set("unity_ColorSpaceDielectricSpec", 0.04f, 0.04f, 0.04f, 1.0f - 0.04f);
                v.Set("unity_ColorSpaceLuminance", 0.0396819152f, 0.458021790f, 0.00609653955f, 1.0f);
            }
            else
            {
                v.Set("unity_ColorSpaceGrey", 0.5f, 0.5f, 0.5f, 0.5f);
                v.Set("unity_ColorSpaceDouble", 2.0f, 2.0f, 2.0f, 2.0f);
                v.Set("unity_ColorSpaceDielectricSpec", 0.220916301f, 0.220916301f, 0.220916301f, 1.0f - 0.220916301f);
                v.Set("unity_ColorSpaceLuminance", 0.22f, 0.707f, 0.071f, 0.0f);
            }
            //one directional light from the upper left front
            var light = Vec3.Normalize(new Vec3(-0.4f, 0.8f, -0.45f));
            v.Set("_WorldSpaceLightPos0", light.X, light.Y, light.Z, 0);
            v.Set("_MainLightPosition", light.X, light.Y, light.Z, 0);
            v.Set("_LightColor0", 1, 0.97f, 0.92f, 1);
            v.Set("_MainLightColor", 1, 0.97f, 0.92f, 1);
            v.Set("_LightColor", 1, 0.97f, 0.92f, 1);
            v.Set("unity_LightData", 1, 0, 1, 0); //URP: z = distance attenuation
            v.Set("_LightShadowData", 0, 0, 0, 0);
            v.Set("_AdditionalLightsCount", 0, 0, 0, 0);
            v.Set("unity_FogParams", 0, 0, 0, 0);
            v.Set("unity_FogColor", 0.5f, 0.5f, 0.5f, 1);
            v.Set("_Color", 1, 1, 1, 1);
            return v;
        }

        /// <summary>The main directional light, coming from the upper left of the camera (a light that follows the view); its direction (toward the light).</summary>
        public Vec3 SetLightFromCamera(Vec3 position, Vec3 target, Vec3 upHint)
        {
            var forward = Vec3.Normalize(target - position);
            var right = Vec3.Normalize(Vec3.Cross(upHint, forward));
            if (!float.IsFinite(right.X))
                right = Vec3.UnitX;
            var up = Vec3.Cross(forward, right);
            var light = Vec3.Normalize(-forward * 0.7f + up * 0.6f - right * 0.4f);
            Set("_WorldSpaceLightPos0", light.X, light.Y, light.Z, 0);
            Set("_MainLightPosition", light.X, light.Y, light.Z, 0);
            //the vertex lights of the legacy passes (Vertex, VertexLit): view space, the directional light first
            if (values.TryGetValue("unity_MatrixV", out var v))
            {
                //column-major: view * (light, 0)
                var x = v[0] * light.X + v[4] * light.Y + v[8] * light.Z;
                var y = v[1] * light.X + v[5] * light.Y + v[9] * light.Z;
                var z = v[2] * light.X + v[6] * light.Y + v[10] * light.Z;
                var positions = new float[8 * 4];
                (positions[0], positions[1], positions[2], positions[3]) = (x, y, z, 0);
                var colors = new float[8 * 4];
                values.TryGetValue("_LightColor0", out var color);
                Array.Copy(color ?? new float[] { 1, 1, 1, 1 }, colors, 4);
                var attenuations = new float[8 * 4];
                for (int i = 0; i < 8; i++)
                    (attenuations[i * 4], attenuations[i * 4 + 1]) = (-1, 1); //not a spot light, no attenuation
                var spots = new float[8 * 4];
                for (int i = 0; i < 8; i++)
                    spots[i * 4 + 2] = 1;
                Set("unity_LightPosition", positions);
                Set("unity_LightColor", colors);
                Set("unity_LightAtten", attenuations);
                Set("unity_SpotDirection", spots);
                //int4: x the light count, y zero, z one (the loops of the fixed function emulation); the bits as they are
                Set("unity_VertexLightParams", BitConverter.Int32BitsToSingle(1), BitConverter.Int32BitsToSingle(0), BitConverter.Int32BitsToSingle(1), BitConverter.Int32BitsToSingle(0));
            }
            return light;
        }

        /// <summary>
        /// The shadow map of a directional light covering a sphere: an orthographic view from the light, depth reversed
        /// (1 nearest the light). Column-major like the matrices of the constant buffers. <paramref name="bias"/> moves the
        /// lookups toward the light; the texture matrix maps x, y to 0..1 (URP's _MainLightWorldToShadow).
        /// </summary>
        public static (float[] clip, float[] lookup, float[] texture) ShadowMatrices(Vec3 light, Vec3 center, float radius, float bias = 0.002f)
        {
            light = Vec3.Normalize(light);
            var upHint = MathF.Abs(light.Y) > 0.99f ? Vec3.UnitZ : Vec3.UnitY;
            var right = Vec3.Normalize(Vec3.Cross(upHint, light));
            var up = Vec3.Cross(light, right);
            radius = Math.Max(radius, 1e-4f) * 1.05f;
            var depthRange = radius * 2.2f;
            //rows of the matrix: x, y in -1..1 across the sphere, z = 0.5 + distance toward the light / depth range
            float[] Matrix(Vec3 x, float x0, Vec3 y, float y0, Vec3 z, float z0) => new[]
            {
                x.X, y.X, z.X, 0,
                x.Y, y.Y, z.Y, 0,
                x.Z, y.Z, z.Z, 0,
                x0, y0, z0, 1,
            };
            Vec3 rx = right / radius, ry = up / radius, rz = light / depthRange;
            float cx = -Vec3.Dot(center, rx), cy = -Vec3.Dot(center, ry), cz = 0.5f - Vec3.Dot(center, rz);
            return (Matrix(rx, cx, ry, cy, rz, cz), Matrix(rx, cx, ry, cy, rz, cz + bias),
                Matrix(rx * 0.5f, cx * 0.5f + 0.5f, ry * 0.5f, cy * 0.5f + 0.5f, rz, cz + bias));
        }

        /// <summary>URP's main light shadows: one cascade, full strength, no fade.</summary>
        public void SetMainLightShadows(float[] worldToShadow, int size)
        {
            Set("_MainLightWorldToShadow", Enumerable.Range(0, 5).SelectMany(_ => worldToShadow).ToArray());
            Set("_MainLightShadowParams", 1, 0, 0, 0);
            Set("_MainLightShadowData", 1, 0, 0, 0);
            Set("_MainLightShadowmapSize", 1f / size, 1f / size, size, size);
            Set("_MainLightShadowCascadeCount", 1, 0, 0, 0);
            Set("_CascadeShadowSplitSphereRadii", 1e8f, 1e8f, 1e8f, 1e8f);
        }

        /// <summary>
        /// Camera and object matrices (Unity world space): a perspective camera at a position looking at a target.
        /// <paramref name="flipY"/>: the projection flipped like a render texture's (_ProjectionParams.x = -1) so that
        /// Direct3D programs come out upright in Vulkan's framebuffer; Unity's Vulkan programs flip their output themselves.
        /// </summary>
        public void SetCamera(Matrix objectToWorld, Vec3 position, Vec3 target, Vec3 upHint, int width, int height, float fieldOfView, bool flipY)
        {
            var distance = Math.Max((target - position).Length(), 1e-5f);
            var forward = Vec3.Normalize(target - position);
            var right = Vec3.Normalize(Vec3.Cross(upHint, forward));
            if (!float.IsFinite(right.X))
                right = Vec3.UnitX;
            var up = Vec3.Cross(forward, right);
            //cameraToWorld (column vectors): columns right, up, forward, position; as the transposed (row) matrix:
            var cameraToWorld = new Matrix(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, position.X, position.Y, position.Z, 1);
            Matrix.Invert(cameraToWorld, out var worldToCameraTransform);
            //Unity's view matrix looks down -Z: flip Z (transposed: multiply on the right)
            var flipZ = Matrix.CreateScale(1, 1, -1);
            var view = worldToCameraTransform * flipZ;
            Matrix.Invert(view, out var inverseView);

            float near = Math.Max(distance * 0.02f, 1e-4f), far = distance * 10f + 10f;
            var aspect = Math.Max(width, 1) / (float)Math.Max(height, 1);
            var f = 1f / MathF.Tan(fieldOfView * MathF.PI / 360f);
            //GL projection, column vectors: rows (f/aspect,0,0,0) (0,f,0,0) (0,0,-(far+near)/(far-near),-2 far near/(far-near)) (0,0,-1,0)
            var glProjection = new Matrix(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, -(far + near) / (far - near), -1, 0, 0, -2 * far * near / (far - near), 0);
            //Direct3D 11 with reversed Z (GL.GetGPUProjectionMatrix): z' = 0.5 w - 0.5 z
            var reversedZ = new Matrix(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -0.5f, 0, 0, 0, 0.5f, 1);
            //rendered like into a render texture (Y flipped, _ProjectionParams.x = -1): Vulkan's framebuffer Y goes down
            var projection = glProjection * reversedZ * (flipY ? Matrix.CreateScale(1, -1, 1) : Matrix.Identity);
            Matrix.Invert(projection, out var inverseProjection);
            Matrix.Invert(objectToWorld, out var worldToObject);

            Set("unity_ObjectToWorld", objectToWorld);
            Set("unity_WorldToObject", worldToObject);
            Set("unity_MatrixV", view);
            Set("unity_MatrixInvV", inverseView);
            Set("unity_WorldToCamera", worldToCameraTransform);
            Set("unity_CameraToWorld", cameraToWorld);
            Set("glstate_matrix_projection", projection);
            Set("unity_MatrixP", projection);
            Set("unity_MatrixInvP", inverseProjection);
            Set("unity_CameraProjection", glProjection);
            Matrix.Invert(glProjection, out var inverseGl);
            Set("unity_CameraInvProjection", inverseGl);
            Set("unity_MatrixVP", view * projection);
            Matrix.Invert(view * projection, out var inverseViewProjection);
            Set("unity_MatrixInvVP", inverseViewProjection);
            Set("glstate_matrix_mvp", objectToWorld * view * projection);
            Set("glstate_matrix_modelview0", objectToWorld * view);
            Matrix.Invert(objectToWorld * view, out var inverseModelView);
            Set("glstate_matrix_invtrans_modelview0", Matrix.Transpose(inverseModelView));
            Set("unity_MatrixPreviousM", objectToWorld);
            Set("unity_MatrixPreviousMI", worldToObject);
            Set("_WorldSpaceCameraPos", position.X, position.Y, position.Z, 1);
            Set("_ProjectionParams", flipY ? -1 : 1, near, far, 1 / far);
            Set("_ScreenParams", width, height, 1 + 1f / width, 1 + 1f / height);
            Set("_ScaledScreenParams", width, height, 1 + 1f / width, 1 + 1f / height);
            //reversed Z
            Set("_ZBufferParams", -1 + far / near, 1, (-1 + far / near) / far, 1 / far);
            Set("unity_OrthoParams", width / (float)height, 1, 0, 0);
        }

        /// <summary>
        /// The material's properties: colors, floats, and for each texture its _ST (tiling, offset), _TexelSize and _HDR.
        /// </summary>
        public void SetMaterial(Material material, Func<string, (int width, int height)?> textureSize, bool linearColorSpace)
        {
            if (material == null)
                return;
            var properties = material.m_SavedProperties;
            foreach (var (name, value) in properties.m_Floats)
                Set(name, value, value, value, value);
            foreach (var (name, color) in properties.m_Colors)
            {
                //material colors are stored in gamma space; a linear project converts them for its shaders (HDR colors too)
                Set(name, linearColorSpace ? GammaToLinear(color.R) : color.R, linearColorSpace ? GammaToLinear(color.G) : color.G, linearColorSpace ? GammaToLinear(color.B) : color.B, color.A);
            }
            foreach (var (name, texEnv) in properties.m_TexEnvs)
            {
                Set(name + "_ST", texEnv.m_Scale.X, texEnv.m_Scale.Y, texEnv.m_Offset.X, texEnv.m_Offset.Y);
                var size = textureSize(name) ?? (4, 4);
                Set(name + "_TexelSize", 1f / size.width, 1f / size.height, size.width, size.height);
                Set(name + "_HDR", 1, 1, 0, 0);
            }
        }

        private static float GammaToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>The contents of a constant buffer: the fields found in the values, zeros elsewhere.</summary>
        public byte[] WriteConstantBuffer(UnityConstantBuffer buffer, int minimumSize)
        {
            var size = Math.Max(buffer.Size, minimumSize);
            size = (size + 15) / 16 * 16;
            var data = new byte[Math.Max(size, 16)];
            foreach (var field in buffer.Fields)
            {
                if (field.Name == null || !values.TryGetValue(field.Name, out var value))
                    continue;
                if (field.IsMatrix)
                {
                    //column-major: 4 columns of 16 bytes, Components rows each
                    var count = Math.Max(1, field.ArraySize);
                    for (int element = 0; element < count && element * 16 < value.Length; element++)
                    {
                        for (int column = 0; column < 4; column++)
                        {
                            for (int row = 0; row < Math.Min(4, Math.Max(field.Components, 1)); row++)
                            {
                                var offset = field.Offset + element * 64 + column * 16 + row * 4;
                                var index = element * 16 + column * 4 + row;
                                if (offset + 4 <= data.Length && index < value.Length)
                                    BitConverter.TryWriteBytes(data.AsSpan(offset), value[index]);
                            }
                        }
                    }
                }
                else
                {
                    var count = Math.Max(1, field.ArraySize);
                    for (int element = 0; element < count; element++)
                    {
                        for (int c = 0; c < Math.Max(field.Components, 1); c++)
                        {
                            var offset = field.Offset + element * 16 + c * 4;
                            var index = element * 4 + c;
                            if (offset + 4 <= data.Length && index < value.Length)
                                BitConverter.TryWriteBytes(data.AsSpan(offset), value[index]);
                        }
                    }
                }
            }
            return data;
        }
    }
}
