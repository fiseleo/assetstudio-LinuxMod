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

        /// <summary>The variant for a material; null with the reason when there is none the preview can draw.</summary>
        public static UnityShaderVariant Select(Shader shader, IReadOnlyCollection<string> materialKeywords, Material material, out string reason)
        {
            reason = null;
            var form = shader?.m_ParsedForm;
            if (form == null || shader.compressedBlob == null)
            {
                reason = "the shader has no compiled programs (Unity 5.5 and up)";
                return null;
            }
            var platform = Array.IndexOf(shader.platforms, ShaderCompilerPlatform.D3D11);
            if (platform < 0)
            {
                reason = $"the shader has no Direct3D 11 programs (platforms: {string.Join(", ", shader.platforms)})";
                return null;
            }
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
                            else if (IsWantedKeyword(keyword))
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
                        static bool IsDx11(ShaderGpuProgramType type) => type >= ShaderGpuProgramType.DX11VertexSM40 && type <= ShaderGpuProgramType.DX11DomainSM50;
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
                    var fragmentSub = Best(pass.progFragment, null);
                    if (fragmentSub == null)
                        continue;
                    var fragmentKeywords = fragmentSub.Keywords;
                    var vertexSub = Best(pass.progVertex, fragmentKeywords);
                    if (vertexSub == null)
                        continue;

                    programs ??= ShaderConverter.ReadShaderProgram(shader, platform);
                    UnityShaderStage Stage(Candidate sub, SerializedProgram program)
                    {
                        if (sub.BlobIndex >= programs.m_SubPrograms.Length || programs.m_SubPrograms[sub.BlobIndex] == null)
                            return null;
                        var code = programs.m_SubPrograms[sub.BlobIndex].m_ProgramCode;
                        var start = FindDxbc(code);
                        if (start < 0)
                            return null;
                        var stage = new UnityShaderStage { ProgramType = sub.Type, Dxbc = code.AsSpan(start).ToArray() };
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
                    var vertex = Stage(vertexSub, pass.progVertex);
                    var fragment = Stage(fragmentSub, pass.progFragment);
                    if (vertex == null || fragment == null)
                        continue;
                    return new UnityShaderVariant
                    {
                        ShaderName = form.m_Name,
                        PassName = pass.m_State?.m_Name,
                        LightMode = lightMode,
                        Keywords = fragmentKeywords.Union(vertexSub.Keywords).ToArray(),
                        Vertex = vertex,
                        Fragment = fragment,
                        State = ReadState(pass.m_State, material),
                    };
                }
            }
            reason ??= "no forward pass with Direct3D 11 vertex and fragment programs";
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
        }

        public List<Resource> Resources { get; } = new List<Resource>();
        public List<int> InputLocations { get; } = new List<int>();

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

            var reflection = new SpirvReflection();
            foreach (var (pointerType, id, storage) in variables)
            {
                if (!types.TryGetValue(pointerType, out var pointer) || pointer.op != 32) //OpTypePointer
                    continue;
                var type = pointer.operands[1];
                if (storage == 1) //Input
                {
                    if (!decorations.ContainsKey((id, 11)) && decorations.TryGetValue((id, 30), out var location)) //not BuiltIn; Location
                        reflection.InputLocations.Add((int)location);
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

        /// <summary>The main directional light, coming from the upper left of the camera (a light that follows the view).</summary>
        public void SetLightFromCamera(Vec3 position, Vec3 target, Vec3 upHint)
        {
            var forward = Vec3.Normalize(target - position);
            var right = Vec3.Normalize(Vec3.Cross(upHint, forward));
            if (!float.IsFinite(right.X))
                right = Vec3.UnitX;
            var up = Vec3.Cross(forward, right);
            var light = Vec3.Normalize(-forward * 0.7f + up * 0.6f - right * 0.4f);
            Set("_WorldSpaceLightPos0", light.X, light.Y, light.Z, 0);
            Set("_MainLightPosition", light.X, light.Y, light.Z, 0);
        }

        /// <summary>Camera and object matrices (Unity world space): a perspective camera at a position looking at a target.</summary>
        public void SetCamera(Matrix objectToWorld, Vec3 position, Vec3 target, Vec3 upHint, int width, int height, float fieldOfView = 30f)
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
            var projection = glProjection * reversedZ * Matrix.CreateScale(1, -1, 1);
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
            Set("_ProjectionParams", -1, near, far, 1 / far);
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
