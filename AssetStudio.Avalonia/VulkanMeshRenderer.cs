using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AssetStudio.Avalonia
{
    using Matrix4x4 = System.Numerics.Matrix4x4;
    using Vector2 = System.Numerics.Vector2;
    using Vector3 = System.Numerics.Vector3;
    using VkBuffer = Silk.NET.Vulkan.Buffer;
    using VkImage = Silk.NET.Vulkan.Image;

    /// <summary>Mesh data uploaded to the GPU (see <see cref="VulkanMeshRenderer.Upload"/>).</summary>
    public sealed class VulkanMesh : IDisposable
    {
        internal VkBuffer VertexBuffer, IndexBuffer, EdgeBuffer;
        internal DeviceMemory VertexMemory, IndexMemory, EdgeMemory;
        internal uint IndexCount, EdgeCount;
        internal bool HasNormals;
        internal VulkanMeshRenderer Owner;
        // textures, one descriptor set each; the last set samples the renderer's white texture (untextured ranges)
        internal VkImage[] TextureImages = Array.Empty<VkImage>();
        internal DeviceMemory[] TextureMemories = Array.Empty<DeviceMemory>();
        internal ImageView[] TextureViews = Array.Empty<ImageView>();
        internal DescriptorPool DescriptorPool;
        internal DescriptorSet[] DescriptorSets = Array.Empty<DescriptorSet>();
        /// <summary>first index, index count, descriptor set</summary>
        internal List<(uint First, uint Count, int Set)> Draws = new();

        public void Dispose()
        {
            Owner?.Free(this);
            Owner = null;
        }
    }

    /// <summary>
    /// Offscreen Vulkan renderer for the mesh / model preview: draws into a multisampled image, resolves it and
    /// reads the pixels back as BGRA, which the preview shows through a WriteableBitmap. Everything runs on the
    /// UI thread. <see cref="Instance"/> is null when no Vulkan device is usable (the software renderer is used
    /// then); set ASSETSTUDIO_RENDERER=software to force that.
    /// </summary>
    public sealed unsafe class VulkanMeshRenderer : IDisposable
    {
        private const Format ColorFormat = Format.B8G8R8A8Unorm;
        private const int PushConstantSize = 128;
        private const int VertexFloats = 8;

        private static VulkanMeshRenderer instance;
        private static bool initialized;

        public static VulkanMeshRenderer Instance
        {
            get
            {
                if (!initialized)
                {
                    initialized = true;
                    if (string.Equals(Environment.GetEnvironmentVariable("ASSETSTUDIO_RENDERER"), "software", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("Model preview: software renderer (ASSETSTUDIO_RENDERER=software)");
                    }
                    else
                    {
                        try
                        {
                            instance = new VulkanMeshRenderer();
                            Logger.Info($"Model preview: Vulkan on {instance.DeviceName} ({(int)instance.samples}x MSAA)");
                        }
                        catch (Exception e)
                        {
                            Logger.Warning($"Vulkan is not available, the model preview uses the software renderer: {e.Message}");
                            instance = null;
                        }
                    }
                }
                return instance;
            }
        }

        /// <summary>Stops using Vulkan after a rendering error, the software renderer takes over.</summary>
        public static void Disable()
        {
            instance?.Dispose();
            instance = null;
            initialized = true;
        }

        private readonly Vk vk;
        private Instance vkInstance;
        private PhysicalDevice physicalDevice;
        private Device device;
        private Queue queue;
        private CommandPool commandPool;
        private CommandBuffer commandBuffer;
        private Fence fence;
        private RenderPass renderPass;
        private PipelineLayout pipelineLayout;
        private DescriptorSetLayout descriptorSetLayout;
        private Sampler sampler;
        private VkImage whiteImage;
        private DeviceMemory whiteMemory;
        private ImageView whiteView;
        private bool canBlitMipmaps;
        private Pipeline fillPipeline, wireOverlayPipeline, wireOnlyPipeline, backgroundPipeline;
        private SampleCountFlags samples;
        private Format depthFormat;
        private PhysicalDeviceMemoryProperties memoryProperties;

        // render target, recreated when the size changes
        private int targetWidth, targetHeight;
        private VkImage colorImage, depthImage, resolveImage;
        private DeviceMemory colorMemory, depthMemory, resolveMemory;
        private ImageView colorView, depthView, resolveView;
        private Framebuffer framebuffer;
        private VkBuffer readbackBuffer;
        private DeviceMemory readbackMemory;
        private void* readbackPointer;

        public string DeviceName { get; private set; }

        private VulkanMeshRenderer()
        {
            vk = Vk.GetApi();
            try
            {
                CreateDevice();
                CreateRenderPass();
                CreatePipelines();
                CreateSampler();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static void Check(Result result, string what)
        {
            if (result != Result.Success)
                throw new Exception($"{what} failed: {result}");
        }

        #region Setup

        private void CreateDevice()
        {
            var appName = (byte*)SilkMarshal.StringToPtr("AssetStudio");
            try
            {
                var appInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = appName,
                    PEngineName = appName,
                    ApiVersion = Vk.Version10,
                };
                var instanceInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &appInfo,
                };
                Check(vk.CreateInstance(in instanceInfo, null, out vkInstance), "vkCreateInstance");
            }
            finally
            {
                SilkMarshal.Free((nint)appName);
            }

            uint count = 0;
            Check(vk.EnumeratePhysicalDevices(vkInstance, ref count, null), "vkEnumeratePhysicalDevices");
            if (count == 0)
                throw new Exception("no Vulkan device found");
            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* pDevices = devices)
                Check(vk.EnumeratePhysicalDevices(vkInstance, ref count, pDevices), "vkEnumeratePhysicalDevices");

            // prefer a real GPU; CPU implementations (lavapipe) are the last resort
            int bestScore = -1;
            uint queueFamily = 0;
            foreach (var candidate in devices)
            {
                vk.GetPhysicalDeviceProperties(candidate, out var props);
                var family = FindGraphicsQueue(candidate);
                if (family < 0)
                    continue;
                var score = props.DeviceType switch
                {
                    PhysicalDeviceType.DiscreteGpu => 4,
                    PhysicalDeviceType.IntegratedGpu => 3,
                    PhysicalDeviceType.VirtualGpu => 2,
                    PhysicalDeviceType.Cpu => 1,
                    _ => 0,
                };
                if (score > bestScore)
                {
                    bestScore = score;
                    physicalDevice = candidate;
                    queueFamily = (uint)family;
                    DeviceName = SilkMarshal.PtrToString((nint)props.DeviceName);
                }
            }
            if (bestScore < 0)
                throw new Exception("no Vulkan device with a graphics queue");

            vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
            vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out memoryProperties);
            var limits = properties.Limits;
            if (limits.MaxPushConstantsSize < PushConstantSize)
                throw new Exception("push constant space too small");
            var sampleCounts = limits.FramebufferColorSampleCounts & limits.FramebufferDepthSampleCounts;
            samples = (sampleCounts & SampleCountFlags.Count4Bit) != 0 ? SampleCountFlags.Count4Bit
                : (sampleCounts & SampleCountFlags.Count2Bit) != 0 ? SampleCountFlags.Count2Bit
                : SampleCountFlags.Count1Bit;

            depthFormat = Format.Undefined;
            foreach (var format in new[] { Format.D32Sfloat, Format.X8D24UnormPack32, Format.D24UnormS8Uint, Format.D16Unorm })
            {
                vk.GetPhysicalDeviceFormatProperties(physicalDevice, format, out var formatProps);
                if ((formatProps.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                {
                    depthFormat = format;
                    break;
                }
            }
            if (depthFormat == Format.Undefined)
                throw new Exception("no depth format");

            float priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            var features = new PhysicalDeviceFeatures();
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                PEnabledFeatures = &features,
            };
            Check(vk.CreateDevice(physicalDevice, in deviceInfo, null, out device), "vkCreateDevice");
            vk.GetDeviceQueue(device, queueFamily, 0, out queue);

            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            Check(vk.CreateCommandPool(device, in poolInfo, null, out commandPool), "vkCreateCommandPool");
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(vk.AllocateCommandBuffers(device, in allocInfo, out commandBuffer), "vkAllocateCommandBuffers");
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Check(vk.CreateFence(device, in fenceInfo, null, out fence), "vkCreateFence");
        }

        private int FindGraphicsQueue(PhysicalDevice candidate)
        {
            uint count = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref count, null);
            var families = new QueueFamilyProperties[count];
            fixed (QueueFamilyProperties* pFamilies = families)
                vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref count, pFamilies);
            for (int i = 0; i < families.Length; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                    return i;
            }
            return -1;
        }

        private void CreateRenderPass()
        {
            bool msaa = samples != SampleCountFlags.Count1Bit;
            var attachments = stackalloc AttachmentDescription[3];
            attachments[0] = new AttachmentDescription
            {
                Format = ColorFormat,
                Samples = samples,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = msaa ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = msaa ? ImageLayout.ColorAttachmentOptimal : ImageLayout.TransferSrcOptimal,
            };
            attachments[1] = new AttachmentDescription
            {
                Format = depthFormat,
                Samples = samples,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.DontCare,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            attachments[2] = new AttachmentDescription
            {
                Format = ColorFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.DontCare,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.TransferSrcOptimal,
            };
            var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
            var depthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
            var resolveRef = new AttachmentReference { Attachment = 2, Layout = ImageLayout.ColorAttachmentOptimal };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorRef,
                PDepthStencilAttachment = &depthRef,
                PResolveAttachments = msaa ? &resolveRef : null,
            };
            // make the color writes visible to the copy into the readback buffer
            var dependency = new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.TransferBit,
                DstAccessMask = AccessFlags.TransferReadBit,
            };
            var info = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = msaa ? 3u : 2u,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(vk.CreateRenderPass(device, in info, null, out renderPass), "vkCreateRenderPass");
        }

        private ShaderModule LoadShader(string name)
        {
            using var stream = typeof(VulkanMeshRenderer).Assembly.GetManifestResourceStream($"Shaders.{name}.spv")
                ?? throw new FileNotFoundException($"embedded shader {name} not found");
            var code = new byte[stream.Length];
            stream.ReadExactly(code);
            fixed (byte* pCode = code)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)pCode,
                };
                Check(vk.CreateShaderModule(device, in info, null, out var module), "vkCreateShaderModule");
                return module;
            }
        }

        private void CreatePipelines()
        {
            var range = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = PushConstantSize,
            };
            var textureBinding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
            var setLayoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &textureBinding,
            };
            Check(vk.CreateDescriptorSetLayout(device, in setLayoutInfo, null, out descriptorSetLayout), "vkCreateDescriptorSetLayout");
            var setLayout = descriptorSetLayout;
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range,
            };
            Check(vk.CreatePipelineLayout(device, in layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");

            var meshVert = LoadShader("mesh.vert");
            var meshFrag = LoadShader("mesh.frag");
            var wireFrag = LoadShader("wire.frag");
            var bgVert = LoadShader("background.vert");
            var bgFrag = LoadShader("background.frag");
            try
            {
                // fill is pushed back a little so the wireframe overlay (depth test <=) wins on its own triangles
                fillPipeline = CreatePipeline(meshVert, meshFrag, true, PrimitiveTopology.TriangleList, true, true, CompareOp.Less, true);
                wireOverlayPipeline = CreatePipeline(meshVert, wireFrag, true, PrimitiveTopology.LineList, true, false, CompareOp.LessOrEqual, false);
                wireOnlyPipeline = CreatePipeline(meshVert, wireFrag, true, PrimitiveTopology.LineList, false, false, CompareOp.Always, false);
                backgroundPipeline = CreatePipeline(bgVert, bgFrag, false, PrimitiveTopology.TriangleList, false, false, CompareOp.Always, false);
            }
            finally
            {
                foreach (var module in new[] { meshVert, meshFrag, wireFrag, bgVert, bgFrag })
                    vk.DestroyShaderModule(device, module, null);
            }
        }

        private Pipeline CreatePipeline(ShaderModule vert, ShaderModule frag, bool vertexInput, PrimitiveTopology topology,
            bool depthTest, bool depthWrite, CompareOp compareOp, bool depthBias)
        {
            var entry = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vert,
                    PName = entry,
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = frag,
                    PName = entry,
                };

                // position (vec3) + normal (vec3) + uv (vec2)
                var binding = new VertexInputBindingDescription { Binding = 0, Stride = VertexFloats * 4, InputRate = VertexInputRate.Vertex };
                var attributes = stackalloc VertexInputAttributeDescription[3];
                attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
                attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 };
                attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 };
                var vertexInputInfo = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = vertexInput ? 1u : 0u,
                    PVertexBindingDescriptions = vertexInput ? &binding : null,
                    VertexAttributeDescriptionCount = vertexInput ? 3u : 0u,
                    PVertexAttributeDescriptions = vertexInput ? attributes : null,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = topology,
                };
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1,
                };
                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1f,
                    DepthBiasEnable = depthBias,
                    DepthBiasConstantFactor = depthBias ? 1f : 0f,
                    DepthBiasSlopeFactor = depthBias ? 1f : 0f,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = samples,
                };
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = depthTest,
                    DepthWriteEnable = depthWrite,
                    DepthCompareOp = compareOp,
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment,
                };
                var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates,
                };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInputInfo,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = pipelineLayout,
                    RenderPass = renderPass,
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

        #endregion

        #region Memory

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred = 0)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var wanted = pass == 0 ? required | preferred : required;
                for (uint i = 0; i < memoryProperties.MemoryTypeCount; i++)
                {
                    if ((typeBits & (1u << (int)i)) != 0 && (memoryProperties.MemoryTypes[(int)i].PropertyFlags & wanted) == wanted)
                        return i;
                }
            }
            throw new Exception("no suitable memory type");
        }

        private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags preferred, out VkBuffer buffer, out DeviceMemory memory)
        {
            var info = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = Math.Max(size, 4),
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(vk.CreateBuffer(device, in info, null, out buffer), "vkCreateBuffer");
            vk.GetBufferMemoryRequirements(device, buffer, out var requirements);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, preferred),
            };
            var result = vk.AllocateMemory(device, in allocInfo, null, out memory);
            if (result != Result.Success)
            {
                vk.DestroyBuffer(device, buffer, null);
                buffer = default;
                Check(result, "vkAllocateMemory");
            }
            Check(vk.BindBufferMemory(device, buffer, memory, 0), "vkBindBufferMemory");
        }

        private void CreateBuffer<T>(ReadOnlySpan<T> data, BufferUsageFlags usage, out VkBuffer buffer, out DeviceMemory memory) where T : unmanaged
        {
            var size = (ulong)(data.Length * sizeof(T));
            CreateBuffer(size, usage, MemoryPropertyFlags.DeviceLocalBit, out buffer, out memory);
            void* mapped;
            Check(vk.MapMemory(device, memory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
            data.CopyTo(new Span<T>(mapped, data.Length));
            vk.UnmapMemory(device, memory);
        }

        private void CreateImage(int width, int height, Format format, SampleCountFlags sampleCount, ImageUsageFlags usage, ImageAspectFlags aspect,
            out VkImage image, out DeviceMemory memory, out ImageView view)
        {
            var info = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D((uint)width, (uint)height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = sampleCount,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(vk.CreateImage(device, in info, null, out image), "vkCreateImage");
            vk.GetImageMemoryRequirements(device, image, out var requirements);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(vk.AllocateMemory(device, in allocInfo, null, out memory), "vkAllocateMemory");
            Check(vk.BindImageMemory(device, image, memory, 0), "vkBindImageMemory");
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
            };
            Check(vk.CreateImageView(device, in viewInfo, null, out view), "vkCreateImageView");
        }

        private void DestroyImage(ref VkImage image, ref DeviceMemory memory, ref ImageView view)
        {
            if (view.Handle != 0) vk.DestroyImageView(device, view, null);
            if (image.Handle != 0) vk.DestroyImage(device, image, null);
            if (memory.Handle != 0) vk.FreeMemory(device, memory, null);
            view = default;
            image = default;
            memory = default;
        }

        private void DestroyBuffer(ref VkBuffer buffer, ref DeviceMemory memory)
        {
            if (buffer.Handle != 0) vk.DestroyBuffer(device, buffer, null);
            if (memory.Handle != 0) vk.FreeMemory(device, memory, null);
            buffer = default;
            memory = default;
        }

        private void EnsureTarget(int width, int height)
        {
            if (width == targetWidth && height == targetHeight && framebuffer.Handle != 0)
                return;
            DestroyTarget();
            bool msaa = samples != SampleCountFlags.Count1Bit;
            CreateImage(width, height, ColorFormat, samples,
                msaa ? ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit : ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                ImageAspectFlags.ColorBit, out colorImage, out colorMemory, out colorView);
            CreateImage(width, height, depthFormat, samples, ImageUsageFlags.DepthStencilAttachmentBit | (msaa ? ImageUsageFlags.TransientAttachmentBit : 0),
                ImageAspectFlags.DepthBit, out depthImage, out depthMemory, out depthView);
            if (msaa)
            {
                CreateImage(width, height, ColorFormat, SampleCountFlags.Count1Bit, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                    ImageAspectFlags.ColorBit, out resolveImage, out resolveMemory, out resolveView);
            }
            var views = stackalloc ImageView[] { colorView, depthView, resolveView };
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = msaa ? 3u : 2u,
                PAttachments = views,
                Width = (uint)width,
                Height = (uint)height,
                Layers = 1,
            };
            Check(vk.CreateFramebuffer(device, in info, null, out framebuffer), "vkCreateFramebuffer");

            CreateBuffer((ulong)width * (ulong)height * 4, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostCachedBit, out readbackBuffer, out readbackMemory);
            void* mapped;
            Check(vk.MapMemory(device, readbackMemory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
            readbackPointer = mapped;
            targetWidth = width;
            targetHeight = height;
        }

        private void DestroyTarget()
        {
            if (device.Handle == 0)
                return;
            if (framebuffer.Handle != 0) vk.DestroyFramebuffer(device, framebuffer, null);
            framebuffer = default;
            DestroyImage(ref colorImage, ref colorMemory, ref colorView);
            DestroyImage(ref depthImage, ref depthMemory, ref depthView);
            DestroyImage(ref resolveImage, ref resolveMemory, ref resolveView);
            if (readbackPointer != null)
                vk.UnmapMemory(device, readbackMemory);
            readbackPointer = null;
            DestroyBuffer(ref readbackBuffer, ref readbackMemory);
            targetWidth = targetHeight = 0;
        }

        #endregion

        #region Textures

        private void CreateSampler()
        {
            vk.GetPhysicalDeviceFormatProperties(physicalDevice, ColorFormat, out var formatProps);
            const FormatFeatureFlags blit = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit | FormatFeatureFlags.SampledImageFilterLinearBit;
            canBlitMipmaps = (formatProps.OptimalTilingFeatures & blit) == blit;
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.Repeat,
                AddressModeV = SamplerAddressMode.Repeat,
                AddressModeW = SamplerAddressMode.Repeat,
                MaxLod = 16,
            };
            Check(vk.CreateSampler(device, in info, null, out sampler), "vkCreateSampler");
            UploadTexture(new PreviewTexture(new byte[] { 255, 255, 255, 255 }, 1, 1), out whiteImage, out whiteMemory, out whiteView);
        }

        /// <summary>Records <paramref name="record"/> into the command buffer, submits it and waits.</summary>
        private void Submit(Action<CommandBuffer> record)
        {
            Check(vk.ResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");
            record(commandBuffer);
            Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");
            var cmd = commandBuffer;
            var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
            Check(vk.QueueSubmit(queue, 1, in submitInfo, fence), "vkQueueSubmit");
            var f = fence;
            Check(vk.WaitForFences(device, 1, &f, true, 10_000_000_000UL), "vkWaitForFences");
            Check(vk.ResetFences(device, 1, &f), "vkResetFences");
        }

        private void ImageBarrier(CommandBuffer cmd, VkImage image, uint mip, uint mipCount, ImageLayout from, ImageLayout to,
            AccessFlags srcAccess, AccessFlags dstAccess, PipelineStageFlags srcStage, PipelineStageFlags dstStage)
        {
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = from,
                NewLayout = to,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, mip, mipCount, 0, 1),
            };
            vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
        }

        /// <summary>Uploads a BGRA texture with a full mip chain (generated on the GPU when the format supports blits).</summary>
        private void UploadTexture(PreviewTexture texture, out VkImage image, out DeviceMemory memory, out ImageView view)
        {
            int width = texture.Width, height = texture.Height;
            var mipLevels = canBlitMipmaps ? (uint)Math.Floor(Math.Log2(Math.Max(width, height))) + 1 : 1u;
            var info = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = ColorFormat,
                Extent = new Extent3D((uint)width, (uint)height, 1),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            image = default;
            memory = default;
            view = default;
            VkBuffer staging = default;
            DeviceMemory stagingMemory = default;
            try
            {
                Check(vk.CreateImage(device, in info, null, out image), "vkCreateImage");
                vk.GetImageMemoryRequirements(device, image, out var requirements);
                var allocInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
                };
                Check(vk.AllocateMemory(device, in allocInfo, null, out memory), "vkAllocateMemory");
                Check(vk.BindImageMemory(device, image, memory, 0), "vkBindImageMemory");

                CreateBuffer<byte>(texture.Bgra.AsSpan(0, width * height * 4), BufferUsageFlags.TransferSrcBit, out staging, out stagingMemory);
                var target = image;
                var source = staging;
                Submit(cmd =>
                {
                    ImageBarrier(cmd, target, 0, mipLevels, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                        0, AccessFlags.TransferWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                    var region = new BufferImageCopy
                    {
                        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                        ImageExtent = new Extent3D((uint)width, (uint)height, 1),
                    };
                    vk.CmdCopyBufferToImage(cmd, source, target, ImageLayout.TransferDstOptimal, 1, in region);
                    int w = width, h = height;
                    for (uint mip = 1; mip < mipLevels; mip++)
                    {
                        ImageBarrier(cmd, target, mip - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
                            AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
                        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                        var blit = new ImageBlit
                        {
                            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip - 1, 0, 1),
                            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, 1),
                        };
                        blit.SrcOffsets[1] = new Offset3D(w, h, 1);
                        blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
                        vk.CmdBlitImage(cmd, target, ImageLayout.TransferSrcOptimal, target, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
                        ImageBarrier(cmd, target, mip - 1, 1, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
                            AccessFlags.TransferReadBit, AccessFlags.ShaderReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
                        w = nw;
                        h = nh;
                    }
                    ImageBarrier(cmd, target, mipLevels - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                        AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
                });

                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = ColorFormat,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipLevels, 0, 1),
                };
                Check(vk.CreateImageView(device, in viewInfo, null, out view), "vkCreateImageView");
            }
            catch
            {
                DestroyImage(ref image, ref memory, ref view);
                throw;
            }
            finally
            {
                DestroyBuffer(ref staging, ref stagingMemory);
            }
        }

        private void CreateDescriptorSets(VulkanMesh mesh)
        {
            var count = (uint)mesh.TextureViews.Length + 1;
            var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = count };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = count,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            Check(vk.CreateDescriptorPool(device, in poolInfo, null, out mesh.DescriptorPool), "vkCreateDescriptorPool");
            var layouts = new DescriptorSetLayout[count];
            Array.Fill(layouts, descriptorSetLayout);
            mesh.DescriptorSets = new DescriptorSet[count];
            fixed (DescriptorSetLayout* pLayouts = layouts)
            fixed (DescriptorSet* pSets = mesh.DescriptorSets)
            {
                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = mesh.DescriptorPool,
                    DescriptorSetCount = count,
                    PSetLayouts = pLayouts,
                };
                Check(vk.AllocateDescriptorSets(device, in allocInfo, pSets), "vkAllocateDescriptorSets");
            }
            for (int i = 0; i < count; i++)
            {
                var imageInfo = new DescriptorImageInfo
                {
                    Sampler = sampler,
                    ImageView = i < mesh.TextureViews.Length ? mesh.TextureViews[i] : whiteView,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = mesh.DescriptorSets[i],
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfo,
                };
                vk.UpdateDescriptorSets(device, 1, in write, 0, null);
            }
        }

        #endregion

        /// <summary>
        /// Uploads a mesh with its textures. Triangles with out of range indices are dropped.
        /// <paramref name="ranges"/> select the texture of each part of <paramref name="indices"/> (-1 = untextured).
        /// </summary>
        public VulkanMesh Upload(Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] indices, DrawRange[] ranges, PreviewTexture[] textures)
        {
            var vertexData = new float[Math.Max(1, vertices.Length) * VertexFloats];
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                var n = normals != null && i < normals.Length ? normals[i] : Vector3.Zero;
                var uv = uvs != null && i < uvs.Length ? uvs[i] : Vector2.Zero;
                var o = i * VertexFloats;
                vertexData[o] = v.X; vertexData[o + 1] = v.Y; vertexData[o + 2] = v.Z;
                vertexData[o + 3] = n.X; vertexData[o + 4] = n.Y; vertexData[o + 5] = n.Z;
                vertexData[o + 6] = uv.X; vertexData[o + 7] = uv.Y;
            }
            var triangles = new uint[indices.Length / 3 * 3];
            var edges = new uint[triangles.Length * 2];
            var mesh = new VulkanMesh { Owner = this, HasNormals = normals != null };
            int t = 0;
            foreach (var range in ranges)
            {
                var first = t;
                for (int i = range.Start; i + 2 < range.Start + range.Count && i + 2 < indices.Length; i += 3)
                {
                    int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                    if ((uint)i0 >= vertices.Length || (uint)i1 >= vertices.Length || (uint)i2 >= vertices.Length)
                        continue;
                    triangles[t] = (uint)i0; triangles[t + 1] = (uint)i1; triangles[t + 2] = (uint)i2;
                    var e = t * 2;
                    edges[e] = (uint)i0; edges[e + 1] = (uint)i1;
                    edges[e + 2] = (uint)i1; edges[e + 3] = (uint)i2;
                    edges[e + 4] = (uint)i2; edges[e + 5] = (uint)i0;
                    t += 3;
                }
                if (t > first)
                    mesh.Draws.Add(((uint)first, (uint)(t - first), range.Texture >= 0 && range.Texture < textures.Length ? range.Texture : textures.Length));
            }
            mesh.IndexCount = (uint)t;
            mesh.EdgeCount = (uint)t * 2;
            try
            {
                CreateBuffer<float>(vertexData, BufferUsageFlags.VertexBufferBit, out mesh.VertexBuffer, out mesh.VertexMemory);
                CreateBuffer<uint>(triangles.AsSpan(0, Math.Max(1, t)), BufferUsageFlags.IndexBufferBit, out mesh.IndexBuffer, out mesh.IndexMemory);
                CreateBuffer<uint>(edges.AsSpan(0, Math.Max(2, t * 2)), BufferUsageFlags.IndexBufferBit, out mesh.EdgeBuffer, out mesh.EdgeMemory);
                mesh.TextureImages = new VkImage[textures.Length];
                mesh.TextureMemories = new DeviceMemory[textures.Length];
                mesh.TextureViews = new ImageView[textures.Length];
                for (int i = 0; i < textures.Length; i++)
                {
                    UploadTexture(textures[i], out mesh.TextureImages[i], out mesh.TextureMemories[i], out mesh.TextureViews[i]);
                }
                CreateDescriptorSets(mesh);
            }
            catch
            {
                Free(mesh);
                throw;
            }
            return mesh;
        }

        /// <summary>
        /// New positions and normals for the vertices of an uploaded mesh (an animation frame). The vertex memory is
        /// host visible and every frame is waited for, so it can be written directly.
        /// </summary>
        public void UpdateVertices(VulkanMesh mesh, Vector3[] vertices, Vector3[] normals)
        {
            void* mapped;
            Check(vk.MapMemory(device, mesh.VertexMemory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
            var data = new Span<float>(mapped, Math.Max(1, vertices.Length) * VertexFloats);
            for (int i = 0; i < vertices.Length; i++)
            {
                var o = i * VertexFloats;
                var v = vertices[i];
                data[o] = v.X; data[o + 1] = v.Y; data[o + 2] = v.Z;
                if (normals != null && i < normals.Length)
                {
                    var n = normals[i];
                    data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
                }
            }
            vk.UnmapMemory(device, mesh.VertexMemory);
        }

        internal void Free(VulkanMesh mesh)
        {
            if (device.Handle == 0)
                return;
            // the renderer waits for each frame, so nothing is in flight here
            DestroyBuffer(ref mesh.VertexBuffer, ref mesh.VertexMemory);
            DestroyBuffer(ref mesh.IndexBuffer, ref mesh.IndexMemory);
            DestroyBuffer(ref mesh.EdgeBuffer, ref mesh.EdgeMemory);
            for (int i = 0; i < mesh.TextureImages.Length; i++)
            {
                DestroyImage(ref mesh.TextureImages[i], ref mesh.TextureMemories[i], ref mesh.TextureViews[i]);
            }
            if (mesh.DescriptorPool.Handle != 0)
                vk.DestroyDescriptorPool(device, mesh.DescriptorPool, null); // frees the sets
            mesh.DescriptorPool = default;
        }

        /// <summary>
        /// Renders the mesh and returns tightly packed BGRA pixels (opaque).
        /// </summary>
        /// <param name="mvp">model -> clip space (row vector convention, as System.Numerics)</param>
        /// <param name="view">model -> view rotation, used for lighting</param>
        /// <param name="wireframeMode">0 = shaded, 1 = shaded + wireframe, 2 = wireframe only</param>
        public byte[] Render(VulkanMesh mesh, int width, int height, Matrix4x4 mvp, Matrix4x4 view, int wireframeMode)
        {
            if (mesh.Owner != this)
                throw new InvalidOperationException("mesh belongs to another renderer");
            EnsureTarget(width, height);

            Check(vk.ResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");

            var clearValues = stackalloc ClearValue[3];
            clearValues[0] = new ClearValue(new ClearColorValue(0.2f, 0.2f, 0.24f, 1f));
            clearValues[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0));
            clearValues[2] = clearValues[0];
            var passInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
                ClearValueCount = samples != SampleCountFlags.Count1Bit ? 3u : 2u,
                PClearValues = clearValues,
            };
            vk.CmdBeginRenderPass(commandBuffer, in passInfo, SubpassContents.Inline);
            var viewport = new Viewport(0, 0, width, height, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
            vk.CmdSetViewport(commandBuffer, 0, 1, in viewport);
            vk.CmdSetScissor(commandBuffer, 0, 1, in scissor);

            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, backgroundPipeline);
            vk.CmdDraw(commandBuffer, 3, 1, 0, 0);

            if (mesh.IndexCount > 0)
            {
                // push constants: mat4 mvp, mat3 view (3 x vec4 columns), vec4 color
                var push = stackalloc float[PushConstantSize / 4];
                *(Matrix4x4*)push = mvp; // row-major row vectors == column-major column vectors in GLSL
                push[16] = view.M11; push[17] = view.M12; push[18] = view.M13; push[19] = 0;
                push[20] = view.M21; push[21] = view.M22; push[22] = view.M23; push[23] = 0;
                push[24] = view.M31; push[25] = view.M32; push[26] = view.M33; push[27] = 0;
                push[28] = wireframeMode == 2 ? 0.85f : 0.16f;
                push[29] = wireframeMode == 2 ? 0.87f : 0.16f;
                push[30] = wireframeMode == 2 ? 0.9f : 0.18f;
                var normalsFlag = mesh.HasNormals ? 1f : 0f;
                push[31] = normalsFlag;
                vk.CmdPushConstants(commandBuffer, pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize, push);

                ulong offset = 0;
                var vertexBuffer = mesh.VertexBuffer;
                vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);
                if (wireframeMode != 2)
                {
                    vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, fillPipeline);
                    vk.CmdBindIndexBuffer(commandBuffer, mesh.IndexBuffer, 0, IndexType.Uint32);
                    foreach (var draw in mesh.Draws)
                    {
                        var set = mesh.DescriptorSets[draw.Set];
                        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, &set, 0, null);
                        // flags in color.a: 1 = normals, 2 = textured
                        var flags = normalsFlag + (draw.Set < mesh.TextureViews.Length ? 2f : 0f);
                        vk.CmdPushConstants(commandBuffer, pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, PushConstantSize - 4, 4, &flags);
                        vk.CmdDrawIndexed(commandBuffer, draw.Count, 1, draw.First, 0, 0);
                    }
                }
                if (wireframeMode != 0)
                {
                    vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, wireframeMode == 1 ? wireOverlayPipeline : wireOnlyPipeline);
                    vk.CmdBindIndexBuffer(commandBuffer, mesh.EdgeBuffer, 0, IndexType.Uint32);
                    vk.CmdDrawIndexed(commandBuffer, mesh.EdgeCount, 1, 0, 0, 0);
                }
            }
            vk.CmdEndRenderPass(commandBuffer);

            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D(0, 0, 0),
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
                Offset = 0,
                Size = Vk.WholeSize,
            };
            vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, in barrier, 0, null);
            Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

            var cmd = commandBuffer;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
            };
            Check(vk.QueueSubmit(queue, 1, in submitInfo, fence), "vkQueueSubmit");
            var f = fence;
            Check(vk.WaitForFences(device, 1, &f, true, 10_000_000_000UL), "vkWaitForFences");
            Check(vk.ResetFences(device, 1, &f), "vkResetFences");

            var pixels = new byte[width * height * 4];
            new ReadOnlySpan<byte>(readbackPointer, pixels.Length).CopyTo(pixels);
            return pixels;
        }

        public void Dispose()
        {
            if (device.Handle != 0)
            {
                vk.DeviceWaitIdle(device);
                DestroyTarget();
                foreach (var pipeline in new[] { fillPipeline, wireOverlayPipeline, wireOnlyPipeline, backgroundPipeline })
                {
                    if (pipeline.Handle != 0)
                        vk.DestroyPipeline(device, pipeline, null);
                }
                if (pipelineLayout.Handle != 0) vk.DestroyPipelineLayout(device, pipelineLayout, null);
                if (descriptorSetLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);
                if (sampler.Handle != 0) vk.DestroySampler(device, sampler, null);
                DestroyImage(ref whiteImage, ref whiteMemory, ref whiteView);
                if (renderPass.Handle != 0) vk.DestroyRenderPass(device, renderPass, null);
                if (fence.Handle != 0) vk.DestroyFence(device, fence, null);
                if (commandPool.Handle != 0) vk.DestroyCommandPool(device, commandPool, null);
                vk.DestroyDevice(device, null);
                device = default;
            }
            if (vkInstance.Handle != 0)
            {
                vk.DestroyInstance(vkInstance, null);
                vkInstance = default;
            }
        }
    }
}
