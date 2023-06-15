using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanRenderPassAttachment
    {
        public Format? ImageFormat { get; set; }
        public SampleCountFlags? Samples { get; set; }
        public AttachmentLoadOp? LoadOp { get; set; }
        public AttachmentStoreOp? StoreOp { get; set; }
        public AttachmentLoadOp? StencilLoadOp { get; set; }
        public AttachmentStoreOp? StencilStoreOp { get; set; }
        public ImageLayout? InitialLayout { get; set; }
        public ImageLayout? FinalLayout { get; set; }
        public ImageLayout? Layout { get; set; }
    }

    internal struct VulkanSubpassDependency
    {
        public PipelineStageFlags SourceStageMask { get; set; }
        public PipelineStageFlags DestinationStageMask { get; set; }
        public AccessFlags SourceAccessMask { get; set; }
        public AccessFlags DestinationAccessMask { get; set; }
    }

    internal struct VulkanRenderPassInfo
    {
        public IReadOnlyList<VulkanRenderPassAttachment>? ColorAttachments { get; set; }
        public VulkanRenderPassAttachment? DepthAttachment { get; set; }
        public VulkanSubpassDependency SubpassDependency { get; set; }
    }

    // referencing https://github.com/yodasoda1219/lighting/blob/main/lib/src/RenderPass.h
    public sealed class VulkanRenderPass : IRenderTarget
    {
        internal unsafe VulkanRenderPass(VulkanDevice device, VulkanRenderPassInfo info)
        {
            int colorAttachmentCount = info.ColorAttachments?.Count ?? 0;
            int depthAttachmentCount = info.DepthAttachment is null ? 0 : 1;

            var colorAttachmentRefs = new AttachmentReference[colorAttachmentCount];
            if (info.ColorAttachments is not null)
            {
                for (int i = 0; i < colorAttachmentCount; i++)
                {
                    colorAttachmentRefs[i] = VulkanUtilities.Init<AttachmentReference>() with
                    {
                        Attachment = (uint)i,
                        Layout = info.ColorAttachments[i].Layout ?? ImageLayout.Undefined
                    };
                }
            }

            AttachmentReference depthAttachmentRef;
            if (info.DepthAttachment is not null)
            {
                depthAttachmentRef = VulkanUtilities.Init<AttachmentReference>() with
                {
                    Attachment = (uint)colorAttachmentCount,
                    Layout = info.DepthAttachment.Value.Layout ?? ImageLayout.Undefined
                };
            }

            mAttachmentTypes = new List<AttachmentType>();
            var attachments = new AttachmentDescription[colorAttachmentCount + depthAttachmentCount];
            for (int i = 0; i < attachments.Length; i++)
            {
                VulkanRenderPassAttachment attachment;
                if (i >= colorAttachmentCount)
                {
                    attachment = info.DepthAttachment!.Value;
                    mAttachmentTypes.Add(AttachmentType.DepthStencil);
                }
                else
                {
                    attachment = info.ColorAttachments![i];
                    mAttachmentTypes.Add(AttachmentType.Color);
                }

                attachments[i] = VulkanUtilities.Init<AttachmentDescription>() with
                {
                    Flags = AttachmentDescriptionFlags.None,
                    Format = attachment.ImageFormat ?? Format.Undefined,
                    Samples = attachment.Samples ?? SampleCountFlags.Count1Bit,
                    LoadOp = attachment.LoadOp ?? AttachmentLoadOp.Clear,
                    StoreOp = attachment.StoreOp ?? AttachmentStoreOp.Store,
                    StencilLoadOp = attachment.StencilLoadOp ?? AttachmentLoadOp.DontCare,
                    StencilStoreOp = attachment.StencilStoreOp ?? AttachmentStoreOp.DontCare,
                    InitialLayout = attachment.InitialLayout ?? ImageLayout.Undefined,
                    FinalLayout = attachment.FinalLayout ?? ImageLayout.Undefined
                };
            }

            var dependency = VulkanUtilities.Init<SubpassDependency>() with
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = info.SubpassDependency.SourceStageMask,
                SrcAccessMask = info.SubpassDependency.SourceAccessMask,
                DstStageMask = info.SubpassDependency.DestinationStageMask,
                DstAccessMask = info.SubpassDependency.DestinationAccessMask
            };

            fixed (AttachmentReference* colorAttachmentRefPtr = colorAttachmentRefs)
            {
                var subpass = VulkanUtilities.Init<SubpassDescription>() with
                {
                    PipelineBindPoint = PipelineBindPoint.Graphics,
                    ColorAttachmentCount = (uint)colorAttachmentCount,
                    PColorAttachments = colorAttachmentRefPtr,
                    PDepthStencilAttachment = info.DepthAttachment is null ? null : &depthAttachmentRef
                };

                fixed (AttachmentDescription* attachmentPtr = attachments)
                {
                    var createInfo = VulkanUtilities.Init<RenderPassCreateInfo>() with
                    {
                        AttachmentCount = (uint)attachments.Length,
                        PAttachments = attachmentPtr,
                        SubpassCount = 1,
                        PSubpasses = &subpass,
                        DependencyCount = 1,
                        PDependencies = &dependency
                    };

                    fixed (RenderPass* renderPass = &mRenderPass)
                    {
                        var api = VulkanContext.API;
                        api.CreateRenderPass(device.Device, &createInfo, null, renderPass).Assert();
                    }
                }
            }

            mDisposed = false;
            mDevice = device;
        }

        ~VulkanRenderPass()
        {
            if (!mDisposed)
            {
                Dispose(false);
                mDisposed = true;
            }
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Dispose(true);
            mDisposed = true;
        }

        private unsafe void Dispose(bool disposing)
        {
            var api = VulkanContext.API;
            api.DestroyRenderPass(mDevice.Device, mRenderPass, null);
        }

        void IRenderTarget.BeginRender(ICommandList commandList, IFramebuffer framebuffer, Vector4 clearColor, bool flipViewport)
        {
            if (commandList is not VulkanCommandBuffer || framebuffer is not VulkanFramebuffer)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            BeginRender((VulkanCommandBuffer)commandList, (VulkanFramebuffer)framebuffer, clearColor, flipViewport);
        }

        public unsafe void BeginRender(VulkanCommandBuffer commandBuffer, VulkanFramebuffer framebuffer, Vector4 clearColor, bool flipViewport = false)
        {
            var clearValues = new ClearValue[mAttachmentTypes.Count];
            for (int i = 0; i < clearValues.Length; i++)
            {
                var clearValue = VulkanUtilities.Init<ClearValue>();
                switch (mAttachmentTypes[i])
                {
                    case AttachmentType.Color:
                        clearValue.Color.Float32_0 = clearColor[0];
                        clearValue.Color.Float32_1 = clearColor[1];
                        clearValue.Color.Float32_2 = clearColor[2];
                        clearValue.Color.Float32_3 = clearColor[3];
                        break;
                    case AttachmentType.DepthStencil:
                        clearValue.DepthStencil.Depth = 1f;
                        clearValue.DepthStencil.Stencil = 0;
                        break;
                    default:
                        throw new ArgumentException("Invalid attachment type!");
                }

                clearValues[i] = clearValue;
            }

            var api = VulkanContext.API;
            var buffer = commandBuffer.Buffer;

            var width = framebuffer.Width;
            var height = framebuffer.Height;

            var scissor = new Rect2D
            {
                Offset = new Offset2D
                {
                    X = 0,
                    Y = 0
                },
                Extent = new Extent2D
                {
                    Width = (uint)width,
                    Height = (uint)height
                }
            };

            fixed (ClearValue* clearValuePtr = clearValues)
            {
                var beginInfo = VulkanUtilities.Init<RenderPassBeginInfo>() with
                {
                    RenderPass = mRenderPass,
                    Framebuffer = framebuffer.Framebuffer,
                    RenderArea = scissor,
                    ClearValueCount = (uint)clearValues.Length,
                    PClearValues = clearValuePtr
                };

                api.CmdBeginRenderPass(buffer, beginInfo, SubpassContents.Inline);
            }

            api.CmdSetScissor(buffer, 0, 1, scissor);
            api.CmdSetViewport(buffer, 0, 1, VulkanUtilities.Init<Viewport>() with
            {
                X = 0f,
                Y = flipViewport ? height : 0f,
                Width = width,
                Height = height * (flipViewport ? -1f : 1f),
                MinDepth = 0f,
                MaxDepth = 1f
            });
        }

        public void EndRender(ICommandList commandList)
        {
            if (commandList is VulkanCommandBuffer commandBuffer)
            {
                var api = VulkanContext.API;
                api.CmdEndRenderPass(commandBuffer.Buffer);
            }
            else
            {
                throw new ArgumentException("Must pass a Vulkan command buffer!");
            }
        }

        public IReadOnlyList<AttachmentType> AttachmentTypes => mAttachmentTypes;
        public RenderPass RenderPass => mRenderPass;

        private readonly RenderPass mRenderPass;
        private readonly VulkanDevice mDevice;

        private readonly List<AttachmentType> mAttachmentTypes;
        private bool mDisposed;
    }
}
