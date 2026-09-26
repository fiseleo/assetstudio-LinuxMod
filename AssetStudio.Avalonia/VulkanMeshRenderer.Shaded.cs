using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace AssetStudio.Avalonia
{
    using Vector2 = System.Numerics.Vector2;
    using Vector3 = System.Numerics.Vector3;
    using Vector4 = System.Numerics.Vector4;
    using VkBuffer = Silk.NET.Vulkan.Buffer;
    using VkImage = Silk.NET.Vulkan.Image;

    /// <summary>The vertex attributes a game's shader can read, in Unity's (left-handed) space.</summary>
    public sealed class ShadedVertices
    {
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Vector4[] Colors;
        public Vector4[] UV0, UV1;
        public Vector2[] UV2, UV3;
    }

    /// <summary>A mesh uploaded for <see cref="VulkanMeshRenderer.RenderShaded"/>.</summary>
    public sealed class ShadedMesh : IDisposable
    {
        internal VkBuffer VertexBuffer, IndexBuffer;
        internal DeviceMemory VertexMemory, IndexMemory;
        internal int VertexCount;
        internal VulkanMeshRenderer Owner;

        public void Dispose()
        {
            Owner?.FreeShaded(this);
            Owner = null;
        }
    }

    /// <summary>Ranges of a <see cref="ShadedMesh"/> drawn with a variant of a game's shader, with its uniforms and textures.</summary>
    public sealed class ShadedDraw : IDisposable
    {
        internal Pipeline Pipeline;
        internal PipelineLayout Layout;
        internal DescriptorSetLayout[] SetLayouts = new DescriptorSetLayout[2];
        internal DescriptorPool Pool;
        internal DescriptorSet[] Sets = new DescriptorSet[2];
        internal List<(VkBuffer buffer, DeviceMemory memory, IntPtr mapped, UnityConstantBuffer constants, int size)> Uniforms = new();
        internal List<(VkImage image, DeviceMemory memory, ImageView view)> Images = new();
        internal List<(uint First, uint Count)> Ranges = new();
        //the descriptors of Unity's screen space shadows (_ShadowMapTexture), pointed at each frame's collection
        internal List<(int Set, uint Binding, DescriptorType Type)> ScreenShadowBindings = new();
        internal VulkanMeshRenderer Owner;
        public UnityShaderVariant Variant { get; internal set; }
        public bool Transparent { get; internal set; }
        /// <summary>For a linear project: sRGB textures sampled as such, drawn into the sRGB target (see RenderShaded).</summary>
        public bool Linear { get; internal set; }

        public void Dispose()
        {
            Owner?.FreeShaded(this);
            Owner = null;
        }
    }

    /// <summary>
    /// The SPIR-V of the variants (vkd3d-shader), cached by the programs' contents: the sub meshes and previews using the
    /// same programs translate them once. Thread-safe, so previews can translate in the background.
    /// </summary>
    public static class ShadedSpirv
    {
        private const int MaxEntries = 512;
        private static readonly ConcurrentDictionary<string, (byte[] vertex, byte[] fragment)> cache = new();

        public static int Count => cache.Count;

        /// <summary>The vertex program (set 0) and the fragment program (its descriptors moved to set 1).</summary>
        public static (byte[] Vertex, byte[] Fragment) Get(UnityShaderVariant variant)
        {
            if (variant.IsVulkan)
                return (variant.Vertex.Spirv, variant.Fragment.Spirv); //the game's own
            if (variant.IsGlsl)
            {
                //compiled together (the stages' inputs and outputs, the default uniform block)
                var glslKey = "glsl" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(variant.Vertex.Glsl + "\0" + variant.Fragment.Glsl)));
                if (cache.TryGetValue(glslKey, out var compiled))
                    return compiled;
                //glslang numbers the bindings of each stage from 0: the fragment program's descriptors go to set 1
                var (vertex, fragment) = Glslang.Compile(variant.Vertex.Glsl, variant.Fragment.Glsl);
                compiled = (vertex, SpirvReflection.WithDescriptorSet(fragment, 1));
                if (cache.Count >= MaxEntries)
                    cache.Clear();
                cache[glslKey] = compiled;
                return compiled;
            }
            var key = Convert.ToHexString(SHA256.HashData(variant.Vertex.Dxbc)) + Convert.ToHexString(SHA256.HashData(variant.Fragment.Dxbc));
            if (cache.TryGetValue(key, out var spirv))
                return spirv;
            spirv = (Vkd3dShader.ToSpirv(variant.Vertex.Dxbc, Vkd3dShader.SourceType.DxbcTpf),
                SpirvReflection.WithDescriptorSet(Vkd3dShader.ToSpirv(variant.Fragment.Dxbc, Vkd3dShader.SourceType.DxbcTpf), 1));
            if (cache.Count >= MaxEntries)
                cache.Clear();
            cache[key] = spirv;
            return spirv;
        }

        public static void Clear() => cache.Clear();
    }

    public sealed unsafe partial class VulkanMeshRenderer
    {
        private const int ShadedVertexFloats = 26; //position 3, normal 3, tangent 4, color 4, uv0 4, uv1 4, uv2 2, uv3 2
        private VkBuffer zeroBuffer;
        private DeviceMemory zeroMemory;
        private readonly Dictionary<(int dimension, uint color, bool srgb), (VkImage image, DeviceMemory memory, ImageView view)> dummyImages = new();
        private readonly Dictionary<(bool srgb, bool array), (VkImage image, DeviceMemory memory, ImageView view)> skyImages = new();

        /// <summary>Uploads the vertices and indices of a mesh drawn with games' shaders.</summary>
        public ShadedMesh UploadShaded(ShadedVertices vertices, int[] indices)
        {
            var count = vertices.Positions.Length;
            var data = new float[Math.Max(1, count) * ShadedVertexFloats];
            for (int i = 0; i < count; i++)
            {
                var o = i * ShadedVertexFloats;
                void Put(int offset, ReadOnlySpan<float> values) => values.CopyTo(data.AsSpan(o + offset));
                var p = vertices.Positions[i];
                Put(0, stackalloc float[] { p.X, p.Y, p.Z });
                var n = vertices.Normals != null && i < vertices.Normals.Length ? vertices.Normals[i] : Vector3.UnitY;
                Put(3, stackalloc float[] { n.X, n.Y, n.Z });
                var t = vertices.Tangents != null && i < vertices.Tangents.Length ? vertices.Tangents[i] : new Vector4(1, 0, 0, 1);
                Put(6, stackalloc float[] { t.X, t.Y, t.Z, t.W });
                var c = vertices.Colors != null && i < vertices.Colors.Length ? vertices.Colors[i] : Vector4.One;
                Put(10, stackalloc float[] { c.X, c.Y, c.Z, c.W });
                var uv0 = vertices.UV0 != null && i < vertices.UV0.Length ? vertices.UV0[i] : Vector4.Zero;
                Put(14, stackalloc float[] { uv0.X, uv0.Y, uv0.Z, uv0.W });
                var uv1 = vertices.UV1 != null && i < vertices.UV1.Length ? vertices.UV1[i] : uv0;
                Put(18, stackalloc float[] { uv1.X, uv1.Y, uv1.Z, uv1.W });
                var uv2 = vertices.UV2 != null && i < vertices.UV2.Length ? vertices.UV2[i] : Vector2.Zero;
                Put(22, stackalloc float[] { uv2.X, uv2.Y });
                var uv3 = vertices.UV3 != null && i < vertices.UV3.Length ? vertices.UV3[i] : Vector2.Zero;
                Put(24, stackalloc float[] { uv3.X, uv3.Y });
            }
            var valid = new List<uint>(indices.Length);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                if ((uint)indices[i] < count && (uint)indices[i + 1] < count && (uint)indices[i + 2] < count)
                {
                    valid.Add((uint)indices[i]);
                    valid.Add((uint)indices[i + 1]);
                    valid.Add((uint)indices[i + 2]);
                }
                else
                {
                    //keep the ranges of the sub meshes: a degenerate triangle
                    valid.Add(0); valid.Add(0); valid.Add(0);
                }
            }
            var mesh = new ShadedMesh { Owner = this, VertexCount = count };
            try
            {
                CreateBuffer<float>(data, BufferUsageFlags.VertexBufferBit, out mesh.VertexBuffer, out mesh.VertexMemory);
                CreateBuffer<uint>(valid.Count > 0 ? valid.ToArray() : new uint[3], BufferUsageFlags.IndexBufferBit, out mesh.IndexBuffer, out mesh.IndexMemory);
            }
            catch
            {
                FreeShaded(mesh);
                throw;
            }
            return mesh;
        }

        /// <summary>New positions and normals of an uploaded mesh (animation, blend shapes); the memory is host visible.</summary>
        public void UpdateShadedPositions(ShadedMesh mesh, Vector3[] positions, Vector3[] normals)
        {
            void* mapped;
            Check(vk.MapMemory(device, mesh.VertexMemory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
            var data = new Span<float>(mapped, Math.Max(1, mesh.VertexCount) * ShadedVertexFloats);
            for (int i = 0; i < mesh.VertexCount && i < positions.Length; i++)
            {
                var o = i * ShadedVertexFloats;
                data[o] = positions[i].X; data[o + 1] = positions[i].Y; data[o + 2] = positions[i].Z;
                if (normals != null && i < normals.Length)
                {
                    data[o + 3] = normals[i].X; data[o + 4] = normals[i].Y; data[o + 5] = normals[i].Z;
                }
            }
            vk.UnmapMemory(device, mesh.VertexMemory);
        }

        //the attribute of a DXBC input semantic: (offset in floats, format); null: no data (reads zeros)
        /// <summary>The attribute of one of Unity's shader channels (0 position, 1 normal, 2 tangent, 3 color, 4.. texture coordinates).</summary>
        private static (uint offset, Format format)? AttributeOfChannel(int channel) => channel switch
        {
            0 => AttributeOf("POSITION", 0),
            1 => AttributeOf("NORMAL", 0),
            2 => AttributeOf("TANGENT", 0),
            3 => AttributeOf("COLOR", 0),
            >= 4 and <= 11 => AttributeOf("TEXCOORD", channel - 4),
            _ => null,
        };

        private static (uint offset, Format format)? AttributeOf(string semantic, int index) => semantic.ToUpperInvariant() switch
        {
            "POSITION" or "SV_POSITION" => (0, Format.R32G32B32Sfloat),
            "NORMAL" => (3, Format.R32G32B32Sfloat),
            "TANGENT" => (6, Format.R32G32B32A32Sfloat),
            "COLOR" => (10, Format.R32G32B32A32Sfloat),
            "TEXCOORD" => index switch
            {
                0 => (14, Format.R32G32B32A32Sfloat),
                1 => (18, Format.R32G32B32A32Sfloat),
                2 => (22, Format.R32G32Sfloat),
                3 => (24, Format.R32G32Sfloat),
                _ => null,
            },
            _ => null,
        };

        //a descriptor of a draw: the resource (of the first stage using it) and every stage using it
        private sealed record Slot(uint Set, SpirvReflection.Resource Resource, int Stage, ShaderStageFlags Stages);

        /// <summary>
        /// Compiles a variant of a game's shader (Direct3D 11 programs through vkd3d-shader, or its Vulkan programs) into
        /// a pipeline drawing index ranges of a mesh. <paramref name="texture"/> gives the texture of a shader texture
        /// parameter (null: Unity's default texture <paramref name="defaultTexture"/> names, e.g. "white", "bump").
        /// </summary>
        public ShadedDraw CreateShadedDraw(UnityShaderVariant variant, IEnumerable<(int First, int Count)> ranges, Func<UnityShaderTexture, PreviewTexture> texture,
            Func<UnityShaderTexture, string> defaultTexture, bool linear = false)
        {
            var (vertexSpirv, fragmentSpirv) = ShadedSpirv.Get(variant);
            var reflections = new[] { SpirvReflection.Read(vertexSpirv), SpirvReflection.Read(fragmentSpirv) };
            var stages = new[] { variant.Vertex, variant.Fragment };
            var comparisonSamplers = stages.Select(x => DxbcSignature.ComparisonSamplers(x.Dxbc)).ToArray();
            var draw = new ShadedDraw { Owner = this, Variant = variant, Transparent = variant.State.Blend, Linear = linear };
            draw.Ranges.AddRange(ranges.Where(x => x.Count > 0).Select(x => ((uint)x.First, (uint)x.Count)));

            //the descriptors by set and binding: vkd3d's programs have set 0 (vertex) and 1 (fragment), Unity's Vulkan
            //programs share their sets between the stages
            var slots = new List<Slot>();
            for (int s = 0; s < 2; s++)
            {
                foreach (var resource in reflections[s].Resources)
                {
                    var flags = s == 0 ? ShaderStageFlags.VertexBit : ShaderStageFlags.FragmentBit;
                    var index = slots.FindIndex(x => x.Set == resource.Set && x.Resource.Binding == resource.Binding);
                    if (index >= 0)
                        slots[index] = slots[index] with { Stages = slots[index].Stages | flags };
                    else
                        slots.Add(new Slot(resource.Set, resource, s, flags));
                }
            }
            var setCount = Math.Max(2, slots.Count == 0 ? 0 : (int)slots.Max(x => x.Set) + 1);
            draw.SetLayouts = new DescriptorSetLayout[setCount];
            draw.Sets = new DescriptorSet[setCount];

            //the parameters of a descriptor: by register (Direct3D), by the set and binding the register names (Vulkan), by
            //the names in the module (OpenGL: the members of the uniform blocks, the samplers)
            bool Names(int register, Slot slot) => variant.IsGlsl
                || (variant.IsVulkan ? UnityShaderVariant.VulkanSlot(register) == (slot.Set, slot.Resource.Binding) : register == slot.Resource.Register);
            IEnumerable<UnityConstantBuffer> BuffersOf(Slot slot) => variant.IsGlsl
                ? new[] { new UnityConstantBuffer { Name = slot.Resource.Name, Register = -1, Size = slot.Resource.Size, Fields = slot.Resource.Fields } }
                : variant.IsVulkan ? stages.SelectMany(x => x.ConstantBuffers) : stages[slot.Stage].ConstantBuffers;
            IEnumerable<UnityShaderTexture> TexturesOf(Slot slot) => variant.IsGlsl
                ? new[] { new UnityShaderTexture { Name = slot.Resource.Name, Register = -1, SamplerRegister = -1, Dimension = slot.Resource.ImageDimension switch { 2 => 3, 3 => 4, _ => 2 } } }
                : variant.IsVulkan ? stages.SelectMany(x => x.Textures) : stages[slot.Stage].Textures;
            static bool IsShadowMap(UnityShaderTexture x) => x.Name == "_MainLightShadowmapTexture" || x.Name == "_AdditionalLightsShadowmapTexture";
            Sampler SamplerOf(Slot slot, int register)
            {
                var users = TexturesOf(slot).Where(x => variant.IsGlsl
                    || (variant.IsVulkan ? UnityShaderVariant.VulkanSlot(x.SamplerRegister) == UnityShaderVariant.VulkanSlot(register) : x.SamplerRegister == register)).ToList();
                //comparison samplers: declared so in the DXBC; the samplers of the shadow maps in Vulkan and OpenGL programs
                var comparison = variant.IsVulkan || variant.IsGlsl ? users.Any(IsShadowMap) : comparisonSamplers[slot.Stage].Contains(register);
                return SamplerFor(users, comparison);
            }

            ShaderModule vertexModule = default, fragmentModule = default;
            try
            {
                for (uint set = 0; set < setCount; set++)
                {
                    var inSet = slots.Where(x => x.Set == set).ToList();
                    var bindings = stackalloc DescriptorSetLayoutBinding[Math.Max(1, inSet.Count)];
                    for (int i = 0; i < inSet.Count; i++)
                    {
                        bindings[i] = new DescriptorSetLayoutBinding
                        {
                            Binding = inSet[i].Resource.Binding,
                            DescriptorType = DescriptorTypeOf(inSet[i].Resource.Kind),
                            DescriptorCount = 1,
                            StageFlags = inSet[i].Stages,
                        };
                    }
                    var layoutInfo = new DescriptorSetLayoutCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)inSet.Count,
                        PBindings = bindings,
                    };
                    Check(vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out draw.SetLayouts[set]), "vkCreateDescriptorSetLayout");
                }
                fixed (DescriptorSetLayout* pSetLayouts = draw.SetLayouts)
                {
                    var pipelineLayoutInfo = new PipelineLayoutCreateInfo
                    {
                        SType = StructureType.PipelineLayoutCreateInfo,
                        SetLayoutCount = (uint)setCount,
                        PSetLayouts = pSetLayouts,
                    };
                    Check(vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out draw.Layout), "vkCreatePipelineLayout");
                }

                //descriptors
                var poolSizes = stackalloc DescriptorPoolSize[4];
                var kinds = new[] { DescriptorType.UniformBuffer, DescriptorType.SampledImage, DescriptorType.Sampler, DescriptorType.CombinedImageSampler };
                for (int k = 0; k < 4; k++)
                {
                    poolSizes[k] = new DescriptorPoolSize { Type = kinds[k], DescriptorCount = (uint)Math.Max(1, slots.Count(x => DescriptorTypeOf(x.Resource.Kind) == kinds[k])) };
                }
                var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)setCount, PoolSizeCount = 4, PPoolSizes = poolSizes };
                Check(vk.CreateDescriptorPool(device, in poolInfo, null, out draw.Pool), "vkCreateDescriptorPool");
                fixed (DescriptorSetLayout* pSetLayouts = draw.SetLayouts)
                fixed (DescriptorSet* pSets = draw.Sets)
                {
                    var allocInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = draw.Pool, DescriptorSetCount = (uint)setCount, PSetLayouts = pSetLayouts };
                    Check(vk.AllocateDescriptorSets(device, in allocInfo, pSets), "vkAllocateDescriptorSets");
                }
                foreach (var slot in slots)
                {
                    var resource = slot.Resource;
                    var write = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = draw.Sets[slot.Set],
                        DstBinding = resource.Binding,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorTypeOf(resource.Kind),
                    };
                    switch (resource.Kind)
                    {
                        case SpirvReflection.ResourceKind.UniformBuffer:
                            {
                                var constants = BuffersOf(slot).FirstOrDefault(x => Names(x.Register, slot));
                                var size = Math.Max(resource.Size, constants?.Size ?? 0);
                                size = Math.Max(16, (size + 15) / 16 * 16);
                                CreateBuffer((ulong)size, BufferUsageFlags.UniformBufferBit, 0, out var buffer, out var memory);
                                void* mapped;
                                Check(vk.MapMemory(device, memory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
                                new Span<byte>(mapped, size).Clear();
                                draw.Uniforms.Add((buffer, memory, (IntPtr)mapped, constants ?? new UnityConstantBuffer { Register = resource.Register }, size));
                                var bufferInfo = new DescriptorBufferInfo { Buffer = buffer, Offset = 0, Range = (ulong)size };
                                write.PBufferInfo = &bufferInfo;
                                vk.UpdateDescriptorSets(device, 1, in write, 0, null);
                                break;
                            }
                        case SpirvReflection.ResourceKind.Sampler:
                            {
                                var register = variant.IsVulkan ? (int)((slot.Set << 16) | resource.Binding) : resource.Register;
                                var imageInfo = new DescriptorImageInfo { Sampler = SamplerOf(slot, register) };
                                write.PImageInfo = &imageInfo;
                                vk.UpdateDescriptorSets(device, 1, in write, 0, null);
                                break;
                            }
                        default:
                            {
                                var parameter = TexturesOf(slot).FirstOrDefault(x => Names(x.Register, slot))
                                    ?? new UnityShaderTexture { Name = "", Register = resource.Register, Dimension = 2 };
                                ImageView view;
                                if (parameter.Name == "_ShadowMapTexture")
                                {
                                    //Unity's screen space shadows: lit until a frame collects them
                                    EnsureShadowResources();
                                    view = whiteView;
                                    draw.ScreenShadowBindings.Add(((int)slot.Set, resource.Binding, DescriptorTypeOf(resource.Kind)));
                                }
                                else if (parameter.Name == "_MainLightShadowmapTexture")
                                {
                                    //URP's shadow map
                                    EnsureShadowResources();
                                    view = shadowView;
                                }
                                else
                                {
                                    view = TextureView(draw, resource, parameter, texture, defaultTexture);
                                }
                                var imageInfo = new DescriptorImageInfo { Sampler = SamplerOf(slot, parameter.SamplerRegister), ImageView = view, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
                                write.PImageInfo = &imageInfo;
                                vk.UpdateDescriptorSets(device, 1, in write, 0, null);
                                break;
                            }
                    }
                }

                vertexModule = CreateModule(vertexSpirv);
                fragmentModule = CreateModule(fragmentSpirv);
                draw.Pipeline = CreateShadedPipeline(variant, draw.Layout, vertexModule, fragmentModule, reflections[0], linear ? renderPassSrgb : renderPass);
            }
            catch
            {
                FreeShaded(draw);
                throw;
            }
            finally
            {
                if (vertexModule.Handle != 0) vk.DestroyShaderModule(device, vertexModule, null);
                if (fragmentModule.Handle != 0) vk.DestroyShaderModule(device, fragmentModule, null);
            }
            return draw;
        }

        /// <summary>The sampler for the textures sampled with it: comparison ones for shadow maps, clamped for the screen space shadows.</summary>
        private Sampler SamplerFor(IReadOnlyCollection<UnityShaderTexture> users, bool comparison)
        {
            if (comparison)
            {
                EnsureShadowResources();
                return compareSampler;
            }
            if (users.Any(x => x.Name == "_ShadowMapTexture"))
            {
                EnsureShadowResources();
                return clampSampler;
            }
            return sampler;
        }

        private static DescriptorType DescriptorTypeOf(SpirvReflection.ResourceKind kind) => kind switch
        {
            SpirvReflection.ResourceKind.UniformBuffer => DescriptorType.UniformBuffer,
            SpirvReflection.ResourceKind.Image => DescriptorType.SampledImage,
            SpirvReflection.ResourceKind.Sampler => DescriptorType.Sampler,
            _ => DescriptorType.CombinedImageSampler,
        };

        private ShaderModule CreateModule(byte[] spirv)
        {
            fixed (byte* p = spirv)
            {
                var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)p };
                Check(vk.CreateShaderModule(device, in info, null, out var module), "vkCreateShaderModule");
                return module;
            }
        }

        /// <summary>
        /// The view of a texture parameter: the material's 2D texture, else a 1x1 image of Unity's default of that name; the
        /// reflection probe (an unnamed cube) is a sky. In a linear project the color ones are sRGB images.
        /// </summary>
        private ImageView TextureView(ShadedDraw draw, SpirvReflection.Resource resource, UnityShaderTexture parameter,
            Func<UnityShaderTexture, PreviewTexture> texture, Func<UnityShaderTexture, string> defaultTexture)
        {
            //SPIR-V Dim: 0 1D, 1 2D, 2 3D, 3 cube
            var dimension = resource.ImageDimension switch { 2 => 3, 3 => resource.Arrayed ? 6 : 4, _ => resource.Arrayed ? 5 : 2 };
            if (dimension == 2)
            {
                var data = texture(parameter);
                if (data != null)
                {
                    UploadTexture(data, out var image, out var memory, out var view, draw.Linear && data.Srgb ? SrgbFormat : ColorFormat);
                    draw.Images.Add((image, memory, view));
                    return view;
                }
            }
            var name = (defaultTexture(parameter) ?? "").ToLowerInvariant();
            if (name == "" && (dimension == 4 || dimension == 6))
                return SkyImage(draw.Linear, dimension == 6);
            //Unity's default textures (BGRA); "bump" and "linearGray" hold data, the others colors
            var color = name switch
            {
                "black" => 0x00000000u,
                "bump" => 0x80FF8080u,
                "gray" or "grey" => 0x80808080u,
                "lineargray" or "lineargrey" => 0x80808080u,
                "red" => 0xFFFF0000u,
                _ => 0xFFFFFFFFu,
            };
            var data_ = name is "bump" or "lineargray" or "lineargrey";
            return DummyImage(dimension, color, draw.Linear && !data_);
        }

        //the sky of the preview, in gamma space: blue grey above, lighter at the horizon, dark below (it matches the ambient)
        private static readonly Vector3 SkyTop = new Vector3(0.36f, 0.42f, 0.52f), SkyHorizon = new Vector3(0.52f, 0.52f, 0.53f), SkyGround = new Vector3(0.22f, 0.21f, 0.2f);

        /// <summary>The sky color (gamma space) toward a direction.</summary>
        public static Vector3 SkyColor(Vector3 direction)
        {
            var y = Math.Clamp(Vector3.Normalize(direction).Y, -1f, 1f);
            return y >= 0 ? Vector3.Lerp(SkyHorizon, SkyTop, MathF.Pow(y, 0.6f)) : Vector3.Lerp(SkyHorizon, SkyGround, MathF.Pow(-y, 0.4f));
        }

        /// <summary>Direction of a texel of a cube face (Vulkan/Direct3D order +X -X +Y -Y +Z -Z).</summary>
        public static Vector3 CubeDirection(int face, float u, float v) => face switch
        {
            0 => new Vector3(1, -v, -u),
            1 => new Vector3(-1, -v, u),
            2 => new Vector3(u, 1, v),
            3 => new Vector3(u, -1, -v),
            4 => new Vector3(u, -v, 1),
            _ => new Vector3(-u, -v, -1),
        };

        /// <summary>
        /// A 64x64 cube of the sky with its mips (the reflection probe: the shaders pick the mip by roughness, Unity's 6 steps).
        /// </summary>
        private ImageView SkyImage(bool srgb, bool array)
        {
            if (skyImages.TryGetValue((srgb, array), out var existing))
                return existing.view;
            const int size = 64;
            var mips = (int)Math.Log2(size) + 1;
            //the faces of each mip, averaged down from the one above
            var levels = new List<float[][]>();
            var top = new float[6][];
            for (int face = 0; face < 6; face++)
            {
                top[face] = new float[size * size * 3];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var color = SkyColor(CubeDirection(face, (x + 0.5f) / size * 2 - 1, (y + 0.5f) / size * 2 - 1));
                        var o = (y * size + x) * 3;
                        (top[face][o], top[face][o + 1], top[face][o + 2]) = (color.X, color.Y, color.Z);
                    }
                }
            }
            levels.Add(top);
            for (int mip = 1, s = size / 2; mip < mips; mip++, s /= 2)
            {
                var above = levels[^1];
                var level = new float[6][];
                for (int face = 0; face < 6; face++)
                {
                    level[face] = new float[s * s * 3];
                    for (int y = 0; y < s; y++)
                        for (int x = 0; x < s; x++)
                            for (int c = 0; c < 3; c++)
                                level[face][(y * s + x) * 3 + c] = (above[face][((2 * y) * s * 2 + 2 * x) * 3 + c] + above[face][((2 * y) * s * 2 + 2 * x + 1) * 3 + c]
                                    + above[face][((2 * y + 1) * s * 2 + 2 * x) * 3 + c] + above[face][((2 * y + 1) * s * 2 + 2 * x + 1) * 3 + c]) / 4;
                }
                levels.Add(level);
            }
            var bytes = new List<byte>();
            var regions = new List<BufferImageCopy>();
            const uint layers = 6; //one cube (a cube array of one)
            for (int mip = 0; mip < mips; mip++)
            {
                var s = size >> mip;
                for (int face = 0; face < 6; face++)
                {
                    regions.Add(new BufferImageCopy
                    {
                        BufferOffset = (ulong)bytes.Count,
                        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mip, (uint)face, 1),
                        ImageExtent = new Extent3D((uint)s, (uint)s, 1),
                    });
                    var pixels = levels[mip][face];
                    for (int i = 0; i < s * s; i++)
                    {
                        bytes.Add((byte)Math.Round(Math.Clamp(pixels[i * 3 + 2], 0, 1) * 255));
                        bytes.Add((byte)Math.Round(Math.Clamp(pixels[i * 3 + 1], 0, 1) * 255));
                        bytes.Add((byte)Math.Round(Math.Clamp(pixels[i * 3], 0, 1) * 255));
                        bytes.Add(255);
                    }
                }
            }
            var format = srgb ? SrgbFormat : ColorFormat;
            var info = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.CreateCubeCompatibleBit,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(size, size, 1),
                MipLevels = (uint)mips,
                ArrayLayers = layers,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(vk.CreateImage(device, in info, null, out var image), "vkCreateImage");
            vk.GetImageMemoryRequirements(device, image, out var requirements);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(vk.AllocateMemory(device, in allocInfo, null, out var memory), "vkAllocateMemory");
            Check(vk.BindImageMemory(device, image, memory, 0), "vkBindImageMemory");
            CreateBuffer<byte>(bytes.ToArray(), BufferUsageFlags.TransferSrcBit, out var staging, out var stagingMemory);
            try
            {
                var copies = regions.ToArray();
                Submit(cmd =>
                {
                    ImageBarrier(cmd, image, 0, (uint)mips, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit,
                        PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, layers);
                    fixed (BufferImageCopy* pCopies = copies)
                        vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, (uint)copies.Length, pCopies);
                    ImageBarrier(cmd, image, 0, (uint)mips, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit,
                        PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, layers);
                });
            }
            finally
            {
                DestroyBuffer(ref staging, ref stagingMemory);
            }
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = array ? ImageViewType.TypeCubeArray : ImageViewType.TypeCube,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, (uint)mips, 0, layers),
            };
            Check(vk.CreateImageView(device, in viewInfo, null, out var view), "vkCreateImageView");
            skyImages[(srgb, array)] = (image, memory, view);
            return view;
        }

        private ImageView DummyImage(int dimension, uint bgra, bool srgb)
        {
            if (dummyImages.TryGetValue((dimension, bgra, srgb), out var existing))
                return existing.view;
            var layers = dimension == 4 || dimension == 6 ? 6u : 1u;
            var info = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = layers == 6 ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
                ImageType = dimension == 3 ? ImageType.Type3D : ImageType.Type2D,
                Format = srgb ? SrgbFormat : ColorFormat,
                Extent = new Extent3D(1, 1, 1),
                MipLevels = 1,
                ArrayLayers = layers,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(vk.CreateImage(device, in info, null, out var image), "vkCreateImage");
            vk.GetImageMemoryRequirements(device, image, out var requirements);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(vk.AllocateMemory(device, in allocInfo, null, out var memory), "vkAllocateMemory");
            Check(vk.BindImageMemory(device, image, memory, 0), "vkBindImageMemory");
            var pixels = new uint[layers];
            Array.Fill(pixels, bgra);
            CreateBuffer<uint>(pixels, BufferUsageFlags.TransferSrcBit, out var staging, out var stagingMemory);
            try
            {
                Submit(cmd =>
                {
                    var barrier = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        OldLayout = ImageLayout.Undefined,
                        NewLayout = ImageLayout.TransferDstOptimal,
                        DstAccessMask = AccessFlags.TransferWriteBit,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = image,
                        SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers),
                    };
                    vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);
                    for (uint layer = 0; layer < layers; layer++)
                    {
                        var region = new BufferImageCopy
                        {
                            BufferOffset = layer * 4,
                            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, layer, 1),
                            ImageExtent = new Extent3D(1, 1, 1),
                        };
                        vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, 1, in region);
                    }
                    barrier.OldLayout = ImageLayout.TransferDstOptimal;
                    barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
                    barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                    barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                    vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, 0, 0, null, 0, null, 1, in barrier);
                });
            }
            finally
            {
                DestroyBuffer(ref staging, ref stagingMemory);
            }
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = dimension switch { 3 => ImageViewType.Type3D, 4 => ImageViewType.TypeCube, 5 => ImageViewType.Type2DArray, 6 => ImageViewType.TypeCube, _ => ImageViewType.Type2D },
                Format = srgb ? SrgbFormat : ColorFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, dimension == 4 || dimension == 6 ? 6u : 1u),
            };
            Check(vk.CreateImageView(device, in viewInfo, null, out var view), "vkCreateImageView");
            dummyImages[(dimension, bgra, srgb)] = (image, memory, view);
            return view;
        }

        private Pipeline CreateShadedPipeline(UnityShaderVariant variant, PipelineLayout layout, ShaderModule vertex, ShaderModule fragment, SpirvReflection vertexReflection, RenderPass pass)
        {
            var entry = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = entry };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = entry };

                //vertex inputs: the input registers of the vertex program (SPIR-V location = register) from the mesh by semantic
                //Direct3D: by the semantics of the input registers (location = register); Vulkan: by Unity's bind channels;
                //OpenGL: by the names of the inputs (in_POSITION0, in_TEXCOORD1...)
                var signature = variant.IsVulkan || variant.IsGlsl ? new List<DxbcSignature.Element>() : DxbcSignature.ReadInputs(variant.Vertex.Dxbc).Where(x => x.SystemValue == 0).ToList();
                var locations = vertexReflection.InputLocations.Distinct().ToList();
                var attributes = stackalloc VertexInputAttributeDescription[Math.Max(1, locations.Count)];
                var usesZero = false;
                for (int i = 0; i < locations.Count; i++)
                {
                    (uint offset, Format format)? attribute;
                    if (variant.IsVulkan)
                    {
                        var input = variant.Vertex.Inputs.FirstOrDefault(x => x.Location == locations[i]);
                        attribute = variant.Vertex.Inputs.Any(x => x.Location == locations[i]) ? AttributeOfChannel(input.Channel) : null;
                    }
                    else if (variant.IsGlsl)
                    {
                        var match = vertexReflection.InputNames.TryGetValue(locations[i], out var inputName)
                            ? System.Text.RegularExpressions.Regex.Match(inputName, @"^in_([A-Z]+)(\d*)$") : System.Text.RegularExpressions.Match.Empty;
                        attribute = match.Success ? AttributeOf(match.Groups[1].Value, match.Groups[2].Value.Length > 0 ? int.Parse(match.Groups[2].Value) : 0) : null;
                    }
                    else
                    {
                        var element = signature.FirstOrDefault(x => x.Register == locations[i]);
                        attribute = element.Semantic != null ? AttributeOf(element.Semantic, element.SemanticIndex) : null;
                    }
                    if (attribute is { } a)
                    {
                        attributes[i] = new VertexInputAttributeDescription { Location = (uint)locations[i], Binding = 0, Format = a.format, Offset = a.offset * 4 };
                    }
                    else
                    {
                        attributes[i] = new VertexInputAttributeDescription { Location = (uint)locations[i], Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 };
                        usesZero = true;
                    }
                }
                var bindings = stackalloc VertexInputBindingDescription[2];
                bindings[0] = new VertexInputBindingDescription { Binding = 0, Stride = ShadedVertexFloats * 4, InputRate = VertexInputRate.Vertex };
                bindings[1] = new VertexInputBindingDescription { Binding = 1, Stride = 0, InputRate = VertexInputRate.Vertex };
                if (usesZero && zeroBuffer.Handle == 0)
                    CreateBuffer<float>(new float[4], BufferUsageFlags.VertexBufferBit, out zeroBuffer, out zeroMemory);
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 2,
                    PVertexBindingDescriptions = bindings,
                    VertexAttributeDescriptionCount = (uint)locations.Count,
                    PVertexAttributeDescriptions = attributes,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
                var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                var state = variant.State;
                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = state.Cull switch { 0 => CullModeFlags.None, 1 => CullModeFlags.FrontBit, _ => CullModeFlags.BackBit },
                    //Direct3D's clockwise front faces; the projection flips Y like a render texture, which keeps the winding
                    FrontFace = FrontFace.Clockwise,
                    LineWidth = 1f,
                };
                var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = samples };
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = state.ZTest != 0 && state.ZTest != 8,
                    DepthWriteEnable = state.ZWrite,
                    //reversed Z (Unity on Direct3D 11): the comparisons swap
                    DepthCompareOp = state.ZTest switch
                    {
                        1 => CompareOp.Never,
                        2 => CompareOp.Greater,
                        3 => CompareOp.Equal,
                        5 => CompareOp.Less,
                        6 => CompareOp.NotEqual,
                        7 => CompareOp.LessOrEqual,
                        8 => CompareOp.Always,
                        _ => CompareOp.GreaterOrEqual,
                    },
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = state.Blend,
                    SrcColorBlendFactor = BlendFactorOf(state.SrcBlend),
                    DstColorBlendFactor = BlendFactorOf(state.DstBlend),
                    ColorBlendOp = BlendOpOf(state.BlendOp),
                    SrcAlphaBlendFactor = BlendFactorOf(state.SrcBlendAlpha),
                    DstAlphaBlendFactor = BlendFactorOf(state.DstBlendAlpha),
                    AlphaBlendOp = BlendOpOf(state.BlendOpAlpha),
                    //the preview is opaque: keep alpha out of the picture
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAttachment };
                var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamicState = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = layout,
                    RenderPass = pass,
                    Subpass = 0,
                };
                Check(vk.CreateGraphicsPipelines(device, default, 1, in info, null, out var pipeline), "vkCreateGraphicsPipelines");
                return pipeline;
            }
            finally
            {
                SilkMarshal.Free((nint)entry);
            }
        }

        //Unity's BlendMode and BlendOp
        private static BlendFactor BlendFactorOf(int mode) => mode switch
        {
            0 => BlendFactor.Zero,
            1 => BlendFactor.One,
            2 => BlendFactor.DstColor,
            3 => BlendFactor.SrcColor,
            4 => BlendFactor.OneMinusDstColor,
            5 => BlendFactor.SrcAlpha,
            6 => BlendFactor.OneMinusSrcColor,
            7 => BlendFactor.DstAlpha,
            8 => BlendFactor.OneMinusDstAlpha,
            9 => BlendFactor.SrcAlphaSaturate,
            10 => BlendFactor.OneMinusSrcAlpha,
            _ => BlendFactor.One,
        };

        private static BlendOp BlendOpOf(int op) => op switch
        {
            1 => BlendOp.Subtract,
            2 => BlendOp.ReverseSubtract,
            3 => BlendOp.Min,
            4 => BlendOp.Max,
            _ => BlendOp.Add,
        };

        /// <summary>Writes the constant buffers of a draw from the values (camera, object, lighting, material).</summary>
        public void UpdateShadedDraw(ShadedDraw draw, UnityShaderValues values)
        {
            foreach (var (_, _, mapped, constants, size) in draw.Uniforms)
            {
                var data = values.WriteConstantBuffer(constants, size);
                new ReadOnlySpan<byte>(data, 0, Math.Min(data.Length, size)).CopyTo(new Span<byte>((void*)mapped, size));
            }
        }

        /// <summary>Renders the draws of a mesh (opaque ones first) and returns tightly packed BGRA pixels.</summary>
        public byte[] RenderShaded(ShadedMesh mesh, IReadOnlyList<ShadedDraw> draws, int width, int height, ShadedShadows shadows = null)
        {
            //the draws of a linear project write linear colors: the sRGB views of the target encode them
            var linear = draws.Count > 0 && draws.All(x => x.Linear);
            EnsureTarget(width, height);
            shadows = shadowPipeline.Handle != 0 ? shadows : null;
            var screenShadows = shadows != null && draws.Any(x => x.ScreenShadowBindings.Count > 0);
            if (screenShadows)
                EnsureCollectTarget(width, height);
            BindScreenShadows(draws, screenShadows);
            Check(vk.ResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");
            if (shadows != null)
                RecordShadows(mesh, draws, shadows, width, height);

            var clearValues = stackalloc ClearValue[3];
            clearValues[0] = linear ? new ClearValue(new ClearColorValue(0.0331f, 0.0331f, 0.0477f, 1f)) : new ClearValue(new ClearColorValue(0.2f, 0.2f, 0.24f, 1f));
            clearValues[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)); //reversed Z
            clearValues[2] = clearValues[0];
            var passInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = linear ? renderPassSrgb : renderPass,
                Framebuffer = linear ? framebufferSrgb : framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
                ClearValueCount = samples != SampleCountFlags.Count1Bit ? 3u : 2u,
                PClearValues = clearValues,
            };
            vk.CmdBeginRenderPass(commandBuffer, in passInfo, SubpassContents.Inline);
            var viewport = new Viewport(0, 0, width, height, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
            vk.CmdSetViewport(commandBuffer, 0, 1, in viewport);
            vk.CmdSetScissor(commandBuffer, 0, 1, in scissor);
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, linear ? backgroundPipelineSrgb : backgroundPipeline);
            vk.CmdDraw(commandBuffer, 3, 1, 0, 0);

            var buffers = stackalloc VkBuffer[] { mesh.VertexBuffer, zeroBuffer.Handle != 0 ? zeroBuffer : mesh.VertexBuffer };
            var offsets = stackalloc ulong[] { 0, 0 };
            vk.CmdBindVertexBuffers(commandBuffer, 0, 2, buffers, offsets);
            vk.CmdBindIndexBuffer(commandBuffer, mesh.IndexBuffer, 0, IndexType.Uint32);
            foreach (var draw in draws.Where(x => !x.Transparent).Concat(draws.Where(x => x.Transparent)))
            {
                if (draw.Pipeline.Handle == 0 || draw.Ranges.Count == 0 || draw.Linear != linear)
                    continue;
                vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, draw.Pipeline);
                fixed (DescriptorSet* sets = draw.Sets)
                    vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, draw.Layout, 0, (uint)draw.Sets.Length, sets, 0, null);
                foreach (var (first, count) in draw.Ranges)
                    vk.CmdDrawIndexed(commandBuffer, count, 1, first, 0, 0);
            }
            vk.CmdEndRenderPass(commandBuffer);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };
            var source = samples != SampleCountFlags.Count1Bit ? resolveImage : colorImage;
            vk.CmdCopyImageToBuffer(commandBuffer, source, ImageLayout.TransferSrcOptimal, readbackBuffer, 1, in region);
            var barrier = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = readbackBuffer,
                Size = Vk.WholeSize,
            };
            vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, in barrier, 0, null);
            Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");
            var cmd = commandBuffer;
            var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
            Check(vk.QueueSubmit(queue, 1, in submitInfo, fence), "vkQueueSubmit");
            var f = fence;
            Check(vk.WaitForFences(device, 1, &f, true, 10_000_000_000UL), "vkWaitForFences");
            Check(vk.ResetFences(device, 1, &f), "vkResetFences");

            var pixels = new byte[width * height * 4];
            new ReadOnlySpan<byte>(readbackPointer, pixels.Length).CopyTo(pixels);
            //the shaders may write any alpha: the preview is opaque
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return pixels;
        }

        internal void FreeShaded(ShadedMesh mesh)
        {
            if (device.Handle == 0)
                return;
            DestroyBuffer(ref mesh.VertexBuffer, ref mesh.VertexMemory);
            DestroyBuffer(ref mesh.IndexBuffer, ref mesh.IndexMemory);
        }

        internal void FreeShaded(ShadedDraw draw)
        {
            if (device.Handle == 0)
                return;
            if (draw.Pipeline.Handle != 0) vk.DestroyPipeline(device, draw.Pipeline, null);
            if (draw.Layout.Handle != 0) vk.DestroyPipelineLayout(device, draw.Layout, null);
            for (int i = 0; i < draw.SetLayouts.Length; i++)
            {
                if (draw.SetLayouts[i].Handle != 0) vk.DestroyDescriptorSetLayout(device, draw.SetLayouts[i], null);
                draw.SetLayouts[i] = default;
            }
            if (draw.Pool.Handle != 0) vk.DestroyDescriptorPool(device, draw.Pool, null);
            for (int i = 0; i < draw.Uniforms.Count; i++)
            {
                var (buffer, memory, _, _, _) = draw.Uniforms[i];
                vk.UnmapMemory(device, memory);
                DestroyBuffer(ref buffer, ref memory);
            }
            draw.Uniforms.Clear();
            for (int i = 0; i < draw.Images.Count; i++)
            {
                var (image, memory, view) = draw.Images[i];
                DestroyImage(ref image, ref memory, ref view);
            }
            draw.Images.Clear();
            draw.Pipeline = default;
            draw.Layout = default;
            draw.Pool = default;
        }

        private void DestroyShadedResources()
        {
            DestroyShadowResources();
            DestroyBuffer(ref zeroBuffer, ref zeroMemory);
            foreach (var key in dummyImages.Keys.ToList())
            {
                var (image, memory, view) = dummyImages[key];
                DestroyImage(ref image, ref memory, ref view);
            }
            dummyImages.Clear();
            foreach (var key in skyImages.Keys.ToList())
            {
                var (image, memory, view) = skyImages[key];
                DestroyImage(ref image, ref memory, ref view);
            }
            skyImages.Clear();
        }
    }
}
