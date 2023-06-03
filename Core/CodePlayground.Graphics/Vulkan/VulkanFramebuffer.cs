using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanFramebufferInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public VulkanRenderPass RenderPass { get; set; }
        public IReadOnlyList<ImageView> Attachments { get; set; }
    }

    public sealed class VulkanFramebuffer : IFramebuffer
    {
        internal unsafe VulkanFramebuffer(VulkanDevice device, VulkanFramebufferInfo info)
        {
            if (info.Attachments.Count != info.RenderPass.AttachmentTypes.Count)
            {
                throw new ArgumentException("Inconsistent attachment count!");
            }

            var images = info.Attachments.ToArray();
            fixed (ImageView* imagePtr = images)
            {
                var createInfo = VulkanUtilities.Init<FramebufferCreateInfo>() with
                {
                    Flags = FramebufferCreateFlags.None,
                    RenderPass = info.RenderPass.RenderPass,
                    AttachmentCount = (uint)images.Length,
                    PAttachments = imagePtr,
                    Width = (uint)info.Width,
                    Height = (uint)info.Height,
                    Layers = 1
                };

                fixed (Framebuffer* framebuffer = &mFramebuffer)
                {
                    var api = VulkanContext.API;
                    api.CreateFramebuffer(device.Device, &createInfo, null, framebuffer);
                }
            }

            mDevice = device;
            mWidth = info.Width;
            mHeight = info.Height;
            
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
            var api = VulkanContext.API;
            api.DestroyFramebuffer(mDevice.Device, mFramebuffer, null);
        }

        public int Width => mWidth;
        public int Height => mHeight;
        public Framebuffer Framebuffer => mFramebuffer;

        private readonly VulkanDevice mDevice;
        private readonly Framebuffer mFramebuffer;

        private readonly int mWidth, mHeight;
        private bool mDisposed;
    }
}