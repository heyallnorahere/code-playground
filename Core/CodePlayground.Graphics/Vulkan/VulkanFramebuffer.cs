using Optick.NET;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanFramebufferAttachmentInfo
    {
        public Image Image { get; set; }
        public Format ImageFormat { get; set; }
        public ImageAspectFlags AspectFlags { get; set; }
        public int Layers { get; set; }
    }

    internal struct VulkanFramebufferInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public VulkanRenderPass RenderPass { get; set; }
        public IReadOnlyList<VulkanFramebufferAttachmentInfo> Attachments { get; set; }
    }

    public sealed class VulkanFramebuffer : IFramebuffer
    {
        internal unsafe VulkanFramebuffer(VulkanDevice device, VulkanFramebufferInfo info)
        {
            using var constructorEvent = OptickMacros.Event();

            if (!info.Attachments.Any())
            {
                throw new ArgumentException("No attachments provided!");
            }

            if (info.Attachments.Count != info.RenderPass.AttachmentTypes.Count)
            {
                throw new ArgumentException("Inconsistent attachment count!");
            }

            int maxLayers = 0;
            var api = VulkanContext.API;

            mViews = new ImageView[info.Attachments.Count];
            for (int i = 0; i < mViews.Length; i++)
            {
                var attachment = info.Attachments[i];
                if (attachment.Layers > maxLayers)
                {
                    maxLayers = attachment.Layers;
                }

                var createInfo = VulkanUtilities.Init<ImageViewCreateInfo>() with
                {
                    Flags = ImageViewCreateFlags.None,
                    Image = attachment.Image,
                    ViewType = attachment.Layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
                    Format = attachment.ImageFormat,
                    Components = VulkanUtilities.Init<ComponentMapping>() with
                    {
                        R = ComponentSwizzle.R,
                        G = ComponentSwizzle.G,
                        B = ComponentSwizzle.B,
                        A = ComponentSwizzle.A
                    },
                    SubresourceRange = VulkanUtilities.Init<ImageSubresourceRange>() with
                    {
                        AspectMask = attachment.AspectFlags,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = (uint)attachment.Layers
                    }
                };

                api.CreateImageView(device.Device, createInfo, null, out mViews[i]).Assert();
            }

            var images = info.Attachments.ToArray();
            fixed (ImageView* imagePtr = mViews)
            {
                var createInfo = VulkanUtilities.Init<FramebufferCreateInfo>() with
                {
                    Flags = FramebufferCreateFlags.None,
                    RenderPass = info.RenderPass.RenderPass,
                    AttachmentCount = (uint)images.Length,
                    PAttachments = imagePtr,
                    Width = (uint)info.Width,
                    Height = (uint)info.Height,
                    Layers = (uint)maxLayers
                };

                fixed (Framebuffer* framebuffer = &mFramebuffer)
                {
                    api.CreateFramebuffer(device.Device, &createInfo, null, framebuffer).Assert();
                }
            }

            mDevice = device;
            mWidth = info.Width;
            mHeight = info.Height;

            mFlip = true;
            mDisposed = false;
        }

        ~VulkanFramebuffer()
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
            using var disposeEvent = OptickMacros.Event();

            var api = VulkanContext.API;
            api.DestroyFramebuffer(mDevice.Device, mFramebuffer, null);

            foreach (var view in mViews)
            {
                api.DestroyImageView(mDevice.Device, view, null);
            }
        }

        public int Width => mWidth;
        public int Height => mHeight;
        public Framebuffer Framebuffer => mFramebuffer;

        public bool Flip
        {
            get => mFlip;
            set => mFlip = value;
        }

        private readonly VulkanDevice mDevice;
        private readonly Framebuffer mFramebuffer;
        private readonly ImageView[] mViews;

        private readonly int mWidth, mHeight;
        private bool mFlip;
        private bool mDisposed;
    }
}