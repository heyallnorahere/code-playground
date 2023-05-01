using Silk.NET.Maths;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanFramebufferInfo
    {
        public Vector2D<int> Size { get; set; }
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
                    Width = (uint)info.Size.X,
                    Height = (uint)info.Size.Y,
                    Layers = 1
                };

                fixed (Framebuffer* framebuffer = &mFramebuffer)
                {
                    var api = VulkanContext.API;
                    api.CreateFramebuffer(device.Device, &createInfo, null, framebuffer);
                }
            }

            mDevice = device;
            mSize = info.Size;
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

        public Vector2D<int> Size => mSize;

        private readonly VulkanDevice mDevice;
        private readonly Framebuffer mFramebuffer;

        private readonly Vector2D<int> mSize;
        private bool mDisposed;
    }
}