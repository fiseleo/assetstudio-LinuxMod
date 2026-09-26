using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia
{
    using VkBuffer = Silk.NET.Vulkan.Buffer;
    using VkImage = Silk.NET.Vulkan.Image;

    /// <summary>
    /// The main light's shadows for the game shaders: matrices in Unity's convention (column-major memory, like the
    /// constant buffers). The shadow map has a reversed depth (nearer the light is greater).
    /// </summary>
    public sealed class ShadedShadows
    {
        /// <summary>World to the shadow map's clip space (x, y in -1..1, depth in 0..1).</summary>
        public float[] LightViewProjection;
        /// <summary>The same with the bias of the lookups (nearer the light, so that surfaces don't shadow themselves).</summary>
        public float[] LightLookup;
        /// <summary>World to the shadow map's texture space with the bias (URP's _MainLightWorldToShadow).</summary>
        public float[] WorldToShadow;
        /// <summary>The camera (the game shaders' unity_MatrixVP).</summary>
        public float[] ViewProjection;
    }

    public sealed unsafe partial class VulkanMeshRenderer
    {
        private const int ShadowSize = 2048;
        private Format shadowFormat;
        private RenderPass shadowPass, collectPass;
        private VkImage shadowImage;
        private DeviceMemory shadowMemory;
        private ImageView shadowView;
        private Framebuffer shadowFramebuffer;
        private PipelineLayout shadowLayout, collectLayout;
        private DescriptorSetLayout collectSetLayout;
        private DescriptorPool collectPool;
        private DescriptorSet collectSet;
        private Pipeline shadowPipeline, collectPipeline;
        private Sampler compareSampler, clampSampler;
        // the screen space shadows (Unity's _ShadowMapTexture), the size of the target
        private int collectWidth, collectHeight;
        private VkImage collectImage, collectDepthImage;
        private DeviceMemory collectMemory, collectDepthMemory;
        private ImageView collectView, collectDepthView;
        private Framebuffer collectFramebuffer;

        /// <summary>The shadow map, its passes and samplers (created the first time a shader receives shadows).</summary>
        private void EnsureShadowResources()
        {
            if (shadowPipeline.Handle != 0)
                return;
            shadowFormat = Format.D16Unorm;
            foreach (var format in new[] { Format.D32Sfloat, Format.D16Unorm })
            {
                vk.GetPhysicalDeviceFormatProperties(physicalDevice, format, out var props);
                const FormatFeatureFlags needed = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit;
                if ((props.OptimalTilingFeatures & needed) == needed)
                {
                    shadowFormat = format;
                    break;
                }
            }
            vk.GetPhysicalDeviceFormatProperties(physicalDevice, shadowFormat, out var shadowProps);
            var linearCompare = (shadowProps.OptimalTilingFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) != 0;

            //samplers: the shadow map compared (lit when the reference is at least the stored depth; outside: lit), the screen texture clamped
            var compareInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = linearCompare ? Filter.Linear : Filter.Nearest,
                MinFilter = linearCompare ? Filter.Linear : Filter.Nearest,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToBorder,
                AddressModeV = SamplerAddressMode.ClampToBorder,
                AddressModeW = SamplerAddressMode.ClampToBorder,
                BorderColor = BorderColor.FloatTransparentBlack,
                CompareEnable = true,
                CompareOp = CompareOp.GreaterOrEqual,
            };
            Check(vk.CreateSampler(device, in compareInfo, null, out compareSampler), "vkCreateSampler");
            var clampInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
            };
            Check(vk.CreateSampler(device, in clampInfo, null, out clampSampler), "vkCreateSampler");

            //the shadow map: depth only, then read by the fragment shaders
            var depthAttachment = new AttachmentDescription
            {
                Format = shadowFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var depthRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.DepthStencilAttachmentOptimal };
            var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, PDepthStencilAttachment = &depthRef };
            var dependency = new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
            };
            var passInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &depthAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(vk.CreateRenderPass(device, in passInfo, null, out shadowPass), "vkCreateRenderPass");

            //the screen space shadows: a color and a depth of the target's size, the color read by the fragment shaders
            var attachments = stackalloc AttachmentDescription[2];
            attachments[0] = new AttachmentDescription
            {
                Format = ColorFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            attachments[1] = new AttachmentDescription
            {
                Format = depthFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.DontCare,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
            var collectDepthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
            var collectSubpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorRef,
                PDepthStencilAttachment = &collectDepthRef,
            };
            var collectDependency = new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
            };
            var collectInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 2,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &collectSubpass,
                DependencyCount = 1,
                PDependencies = &collectDependency,
            };
            Check(vk.CreateRenderPass(device, in collectInfo, null, out collectPass), "vkCreateRenderPass");

            //the shadow map image, cleared (lit everywhere) and readable until the first shadow pass
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = shadowFormat,
                Extent = new Extent3D(ShadowSize, ShadowSize, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(vk.CreateImage(device, in imageInfo, null, out shadowImage), "vkCreateImage");
            vk.GetImageMemoryRequirements(device, shadowImage, out var requirements);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(vk.AllocateMemory(device, in allocInfo, null, out shadowMemory), "vkAllocateMemory");
            Check(vk.BindImageMemory(device, shadowImage, shadowMemory, 0), "vkBindImageMemory");
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = shadowImage,
                ViewType = ImageViewType.Type2D,
                Format = shadowFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            };
            Check(vk.CreateImageView(device, in viewInfo, null, out shadowView), "vkCreateImageView");
            var image = shadowImage;
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
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
                };
                vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);
                var clear = new ClearDepthStencilValue(0f, 0);
                var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1);
                vk.CmdClearDepthStencilImage(cmd, image, ImageLayout.TransferDstOptimal, in clear, 1, in range);
                barrier.OldLayout = ImageLayout.TransferDstOptimal;
                barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in barrier);
            });
            var view = shadowView;
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = shadowPass,
                AttachmentCount = 1,
                PAttachments = &view,
                Width = ShadowSize,
                Height = ShadowSize,
                Layers = 1,
            };
            Check(vk.CreateFramebuffer(device, in framebufferInfo, null, out shadowFramebuffer), "vkCreateFramebuffer");

            //layouts: the light's matrix; the camera's and the light's, with the shadow map
            var shadowRange = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Size = 64 };
            var shadowLayoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, PushConstantRangeCount = 1, PPushConstantRanges = &shadowRange };
            Check(vk.CreatePipelineLayout(device, in shadowLayoutInfo, null, out shadowLayout), "vkCreatePipelineLayout");
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
            var setLayoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
            Check(vk.CreateDescriptorSetLayout(device, in setLayoutInfo, null, out collectSetLayout), "vkCreateDescriptorSetLayout");
            var collectRange = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Size = 128 };
            var setLayout = collectSetLayout;
            var collectLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &collectRange,
            };
            Check(vk.CreatePipelineLayout(device, in collectLayoutInfo, null, out collectLayout), "vkCreatePipelineLayout");
            var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
            var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &poolSize };
            Check(vk.CreateDescriptorPool(device, in poolInfo, null, out collectPool), "vkCreateDescriptorPool");
            var setAlloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = collectPool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
            Check(vk.AllocateDescriptorSets(device, in setAlloc, out collectSet), "vkAllocateDescriptorSets");
            var shadowImageInfo = new DescriptorImageInfo { Sampler = compareSampler, ImageView = shadowView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = collectSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &shadowImageInfo,
            };
            vk.UpdateDescriptorSets(device, 1, in write, 0, null);

            var depthVert = LoadShader("shadow_depth.vert");
            var collectVert = LoadShader("shadow_collect.vert");
            var collectFrag = LoadShader("shadow_collect.frag");
            try
            {
                shadowPipeline = CreateShadowPipeline(depthVert, default, shadowLayout, shadowPass, true);
                collectPipeline = CreateShadowPipeline(collectVert, collectFrag, collectLayout, collectPass, false);
            }
            finally
            {
                foreach (var module in new[] { depthVert, collectVert, collectFrag })
                    vk.DestroyShaderModule(device, module, null);
            }
        }

        /// <summary>A pipeline drawing the positions of a shaded mesh (reversed depth, both faces).</summary>
        private Pipeline CreateShadowPipeline(ShaderModule vertex, ShaderModule fragment, PipelineLayout layout, RenderPass pass, bool depthOnly)
        {
            var entry = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = entry };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = entry };
                var binding = new VertexInputBindingDescription { Binding = 0, Stride = ShadedVertexFloats * 4, InputRate = VertexInputRate.Vertex };
                var attribute = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 1,
                    PVertexAttributeDescriptions = &attribute,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
                var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.Clockwise,
                    LineWidth = 1f,
                    //the stored depth pushed away from the light (reversed: smaller) on slopes, against acne
                    DepthBiasEnable = depthOnly,
                    DepthBiasSlopeFactor = depthOnly ? -1.5f : 0f,
                };
                var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.Greater,
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = depthOnly ? 0u : 1u,
                    PAttachments = depthOnly ? null : &blendAttachment,
                };
                var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamicState = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = depthOnly ? 1u : 2u,
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

        /// <summary>The screen space shadows target, of the size of the render target.</summary>
        private void EnsureCollectTarget(int width, int height)
        {
            if (collectFramebuffer.Handle != 0 && collectWidth == width && collectHeight == height)
                return;
            DestroyCollectTarget();
            CreateImage(width, height, ColorFormat, SampleCountFlags.Count1Bit, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
                ImageAspectFlags.ColorBit, out collectImage, out collectMemory, out collectView);
            CreateImage(width, height, depthFormat, SampleCountFlags.Count1Bit, ImageUsageFlags.DepthStencilAttachmentBit,
                ImageAspectFlags.DepthBit, out collectDepthImage, out collectDepthMemory, out collectDepthView);
            var views = stackalloc ImageView[] { collectView, collectDepthView };
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = collectPass,
                AttachmentCount = 2,
                PAttachments = views,
                Width = (uint)width,
                Height = (uint)height,
                Layers = 1,
            };
            Check(vk.CreateFramebuffer(device, in info, null, out collectFramebuffer), "vkCreateFramebuffer");
            collectWidth = width;
            collectHeight = height;
        }

        private void DestroyCollectTarget()
        {
            if (collectFramebuffer.Handle != 0) vk.DestroyFramebuffer(device, collectFramebuffer, null);
            collectFramebuffer = default;
            DestroyImage(ref collectImage, ref collectMemory, ref collectView);
            DestroyImage(ref collectDepthImage, ref collectDepthMemory, ref collectDepthView);
            collectWidth = collectHeight = 0;
        }

        /// <summary>
        /// Records the shadow map of the opaque draws and, when a draw reads Unity's screen space shadows, their collection
        /// (the attenuation of every pixel of the camera's view).
        /// </summary>
        private void RecordShadows(ShadedMesh mesh, IReadOnlyList<ShadedDraw> draws, ShadedShadows shadows, int width, int height)
        {
            var casters = draws.Where(x => !x.Transparent && x.Pipeline.Handle != 0).ToList();
            var clear = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0));
            var passInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = shadowPass,
                Framebuffer = shadowFramebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(ShadowSize, ShadowSize)),
                ClearValueCount = 1,
                PClearValues = &clear,
            };
            vk.CmdBeginRenderPass(commandBuffer, in passInfo, SubpassContents.Inline);
            var viewport = new Viewport(0, 0, ShadowSize, ShadowSize, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(ShadowSize, ShadowSize));
            vk.CmdSetViewport(commandBuffer, 0, 1, in viewport);
            vk.CmdSetScissor(commandBuffer, 0, 1, in scissor);
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, shadowPipeline);
            fixed (float* light = shadows.LightViewProjection)
                vk.CmdPushConstants(commandBuffer, shadowLayout, ShaderStageFlags.VertexBit, 0, 64, light);
            DrawPositions(mesh, casters);
            vk.CmdEndRenderPass(commandBuffer);

            if (!draws.Any(x => x.ScreenShadowBindings.Count > 0))
                return;
            var clears = stackalloc ClearValue[2];
            clears[0] = new ClearValue(new ClearColorValue(1f, 1f, 1f, 1f)); //lit
            clears[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0));
            var collectInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = collectPass,
                Framebuffer = collectFramebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
                ClearValueCount = 2,
                PClearValues = clears,
            };
            vk.CmdBeginRenderPass(commandBuffer, in collectInfo, SubpassContents.Inline);
            viewport = new Viewport(0, 0, width, height, 0, 1);
            scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
            vk.CmdSetViewport(commandBuffer, 0, 1, in viewport);
            vk.CmdSetScissor(commandBuffer, 0, 1, in scissor);
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, collectPipeline);
            var set = collectSet;
            vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, collectLayout, 0, 1, &set, 0, null);
            var matrices = shadows.ViewProjection.Concat(shadows.LightLookup).ToArray();
            fixed (float* push = matrices)
                vk.CmdPushConstants(commandBuffer, collectLayout, ShaderStageFlags.VertexBit, 0, 128, push);
            DrawPositions(mesh, casters.Concat(draws.Where(x => x.Transparent && x.Pipeline.Handle != 0)).ToList());
            vk.CmdEndRenderPass(commandBuffer);
        }

        private void DrawPositions(ShadedMesh mesh, IReadOnlyList<ShadedDraw> draws)
        {
            var buffer = mesh.VertexBuffer;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &buffer, &offset);
            vk.CmdBindIndexBuffer(commandBuffer, mesh.IndexBuffer, 0, IndexType.Uint32);
            foreach (var draw in draws)
            {
                foreach (var (first, count) in draw.Ranges)
                    vk.CmdDrawIndexed(commandBuffer, count, 1, first, 0, 0);
            }
        }

        /// <summary>Points the draws reading Unity's screen space shadows at the current collection target.</summary>
        private void BindScreenShadows(IReadOnlyList<ShadedDraw> draws, bool collected)
        {
            foreach (var draw in draws)
            {
                foreach (var (set, binding, type) in draw.ScreenShadowBindings)
                {
                    var imageInfo = new DescriptorImageInfo { Sampler = clampSampler, ImageView = collected ? collectView : whiteView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
                    var write = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = draw.Sets[set],
                        DstBinding = binding,
                        DescriptorCount = 1,
                        DescriptorType = type,
                        PImageInfo = &imageInfo,
                    };
                    vk.UpdateDescriptorSets(device, 1, in write, 0, null);
                }
            }
        }

        private void DestroyShadowResources()
        {
            DestroyCollectTarget();
            foreach (var pipeline in new[] { shadowPipeline, collectPipeline })
            {
                if (pipeline.Handle != 0)
                    vk.DestroyPipeline(device, pipeline, null);
            }
            shadowPipeline = collectPipeline = default;
            if (shadowLayout.Handle != 0) vk.DestroyPipelineLayout(device, shadowLayout, null);
            if (collectLayout.Handle != 0) vk.DestroyPipelineLayout(device, collectLayout, null);
            shadowLayout = collectLayout = default;
            if (collectPool.Handle != 0) vk.DestroyDescriptorPool(device, collectPool, null);
            collectPool = default;
            if (collectSetLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, collectSetLayout, null);
            collectSetLayout = default;
            if (shadowFramebuffer.Handle != 0) vk.DestroyFramebuffer(device, shadowFramebuffer, null);
            shadowFramebuffer = default;
            DestroyImage(ref shadowImage, ref shadowMemory, ref shadowView);
            if (shadowPass.Handle != 0) vk.DestroyRenderPass(device, shadowPass, null);
            if (collectPass.Handle != 0) vk.DestroyRenderPass(device, collectPass, null);
            shadowPass = collectPass = default;
            if (compareSampler.Handle != 0) vk.DestroySampler(device, compareSampler, null);
            if (clampSampler.Handle != 0) vk.DestroySampler(device, clampSampler, null);
            compareSampler = clampSampler = default;
        }
    }
}
