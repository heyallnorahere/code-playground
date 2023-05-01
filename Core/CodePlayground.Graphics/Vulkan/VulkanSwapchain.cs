﻿using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanSwapchainFramebuffer
    {
        public Image Image { get; set; }
        public ImageView View { get; set; }
        public VulkanFramebuffer Framebuffer { get; set; }
    }

    public sealed class VulkanSwapchain : ISwapchain, IDisposable
    {
        public static unsafe int FindPresentQueueFamily(KhrSurface extension, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            var queueFamilies = physicalDevice.GetQueueFamilyProperties();
            for (int i = 0; i < queueFamilies.Count; i++)
            {
                Bool32 supported = false;
                extension.GetPhysicalDeviceSurfaceSupport(physicalDevice.Device, (uint)i, surface, &supported).Assert();

                if (supported)
                {
                    return i;
                }
            }

            throw new ArgumentException("Failed to find present queue family!");
        }

        public static SurfaceCapabilitiesKHR QuerySurfaceCapabilities(KhrSurface extension, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            extension.GetPhysicalDeviceSurfaceCapabilities(physicalDevice.Device, surface, out SurfaceCapabilitiesKHR capabilities).Assert();
            return capabilities;
        }

        public static unsafe IReadOnlyList<SurfaceFormatKHR> QuerySurfaceFormats(KhrSurface extension, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            uint formatCount = 0;
            extension.GetPhysicalDeviceSurfaceFormats(physicalDevice.Device, surface, &formatCount, null).Assert();

            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* ptr = formats)
            {
                extension.GetPhysicalDeviceSurfaceFormats(physicalDevice.Device, surface, &formatCount, ptr);
            }

            return formats;
        }

        public static unsafe IReadOnlyList<PresentModeKHR> QuerySurfacePresentModes(KhrSurface extension, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            uint presentModeCount = 0;
            extension.GetPhysicalDeviceSurfacePresentModes(physicalDevice.Device, surface, &presentModeCount, null).Assert();

            var presentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* ptr = presentModes)
            {
                extension.GetPhysicalDeviceSurfacePresentModes(physicalDevice.Device, surface, &presentModeCount, ptr);
            }

            return presentModes;
        }

        internal VulkanSwapchain(SurfaceKHR surface, VulkanDevice device, Instance instance, IWindow window)
        {
            var api = VulkanContext.API;
            mSwapchainExtension = api.GetDeviceExtension<KhrSwapchain>(instance, device.Device);
            mSurfaceExtension = api.GetInstanceExtension<KhrSurface>(instance);
            mDisposed = false;
            mVSync = false;

            mPresentQueueFamily = FindPresentQueueFamily(mSurfaceExtension, device.PhysicalDevice, surface);
            mPresentQueue = device.GetQueue(mPresentQueueFamily);
            mDevice = device;
            mInstance = instance;
            mWindow = window;

            var framebufferSize = window.FramebufferSize;
            var extent = new Extent2D
            {
                Width = (uint)framebufferSize.X,
                Height = (uint)framebufferSize.Y
            };

            mSurface = surface;
            Create(extent);

            window.FramebufferResize += Resize;
        }

        ~VulkanSwapchain()
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
            if (disposing)
            {
                mWindow.FramebufferResize -= Resize;
            }

            DestroyFramebuffers();
            mRenderPass.Dispose();

            mSwapchainExtension.DestroySwapchain(mDevice.Device, mSwapchain, null);
            mSurfaceExtension.DestroySurface(mInstance, mSurface, null);
        }

        private void Resize(Vector2D<int> windowSize)
        {
            mExtent = new Extent2D
            {
                Width = (uint)windowSize.X,
                Height = (uint)windowSize.Y
            };

            Invalidate();
        }

        public unsafe void Invalidate()
        {
            DestroyFramebuffers();

            var old = Create(mExtent);
            if (old.Handle != 0)
            {
                mSwapchainExtension.DestroySwapchain(mDevice.Device, old, null);
            }

            SwapchainInvalidated?.Invoke(Size);
        }

        private SurfaceFormatKHR ChooseSurfaceFormat()
        {
            var formats = QuerySurfaceFormats(mSurfaceExtension, mDevice.PhysicalDevice, mSurface);
            foreach (var format in formats)
            {
                if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }

            // https://registry.khronos.org/vulkan/specs/1.3-extensions/man/html/vkGetPhysicalDeviceSurfaceFormatsKHR.html
            return formats[0];
        }

        private PresentModeKHR ChoosePresentMode()
        {
            if (!mVSync)
            {
                var presentModes = QuerySurfacePresentModes(mSurfaceExtension, mDevice.PhysicalDevice, mSurface);
                foreach (var presentMode in presentModes)
                {
                    if (presentMode == PresentModeKHR.MailboxKhr)
                    {
                        return presentMode;
                    }
                }
            }

            return PresentModeKHR.FifoKhr;
        }

        private Extent2D ChooseExtent(Extent2D passedExtent, SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return capabilities.CurrentExtent;
            }
            else
            {
                return new Extent2D
                {
                    Width = Math.Clamp(passedExtent.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                    Height = Math.Clamp(passedExtent.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
                };
            }
        }

        [MemberNotNull(nameof(mRenderPass), nameof(mFramebuffers))]
        private unsafe SwapchainKHR Create(Extent2D extent)
        {
            var queueFamilyIndices = mDevice.PhysicalDevice.FindQueueTypes();
            var graphicsFamily = queueFamilyIndices[CommandQueueFlags.Graphics];
            var queueFamilies = new uint[] { (uint)graphicsFamily, (uint)mPresentQueueFamily };

            var capabilities = QuerySurfaceCapabilities(mSurfaceExtension, mDevice.PhysicalDevice, mSurface);
            var imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            {
                imageCount = capabilities.MaxImageCount;
            }

            var format = ChooseSurfaceFormat();
            var createInfo = VulkanUtilities.Init<SwapchainCreateInfoKHR>() with
            {
                Surface = mSurface,
                MinImageCount = imageCount,
                ImageFormat = mImageFormat = format.Format,
                ImageColorSpace = format.ColorSpace,
                ImageExtent = mExtent = ChooseExtent(extent, capabilities),
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = ChoosePresentMode(),
                Clipped = true,
                OldSwapchain = mSwapchain
            };

            var old = mSwapchain;
            fixed (uint* queueFamilyPtr = queueFamilies)
            {
                if (graphicsFamily != mPresentQueueFamily)
                {
                    createInfo.ImageSharingMode = SharingMode.Concurrent;
                    createInfo.QueueFamilyIndexCount = (uint)queueFamilies.Length; // 2
                    createInfo.PQueueFamilyIndices = queueFamilyPtr;
                }
                else
                {
                    createInfo.ImageSharingMode = SharingMode.Exclusive;
                    createInfo.QueueFamilyIndexCount = 0;
                    createInfo.PQueueFamilyIndices = null;
                }

                fixed (SwapchainKHR* swapchain = &mSwapchain)
                {
                    mSwapchainExtension.CreateSwapchain(mDevice.Device, &createInfo, null, swapchain).Assert();
                }
            }

            mRenderPass ??= new VulkanRenderPass(mDevice, new VulkanRenderPassInfo
            {
                ColorAttachments = new VulkanRenderPassAttachment[]
                {
                    new VulkanRenderPassAttachment
                    {
                        ImageFormat = mImageFormat,
                        Samples = SampleCountFlags.Count1Bit,
                        LoadOp = AttachmentLoadOp.Clear,
                        StoreOp = AttachmentStoreOp.Store,
                        StencilLoadOp = AttachmentLoadOp.DontCare,
                        StencilStoreOp = AttachmentStoreOp.DontCare,
                        InitialLayout = ImageLayout.Undefined,
                        FinalLayout = ImageLayout.PresentSrcKhr,
                        Layout = ImageLayout.ColorAttachmentOptimal
                    }
                },
                SubpassDependency = new VulkanSubpassDependency // todo: add flags for depth attachment
                {
                    SourceStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                    SourceAccessMask = 0,
                    DestinationStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                    DestinationAccessMask = AccessFlags.ColorAttachmentWriteBit
                }
            });

            mFramebuffers = CreateFramebuffers();
            return old;
        }

        private unsafe VulkanSwapchainFramebuffer[] CreateFramebuffers()
        {
            uint imageCount = 0;
            mSwapchainExtension.GetSwapchainImages(mDevice.Device, mSwapchain, &imageCount, null);

            var images = new Image[imageCount];
            fixed (Image* imagePtr = images)
            {
                mSwapchainExtension.GetSwapchainImages(mDevice.Device, mSwapchain, &imageCount, imagePtr);
            }

            var framebuffers = new VulkanSwapchainFramebuffer[imageCount];
            var api = VulkanContext.API;
            for (uint i = 0; i < imageCount; i++)
            {
                var viewInfo = VulkanUtilities.Init<ImageViewCreateInfo>() with
                {
                    Flags = ImageViewCreateFlags.None,
                    Image = images[i],
                    ViewType = ImageViewType.Type2D,
                    Format = mImageFormat,
                    Components = VulkanUtilities.Init<ComponentMapping>() with
                    {
                        R = ComponentSwizzle.R,
                        G = ComponentSwizzle.G,
                        B = ComponentSwizzle.B,
                        A = ComponentSwizzle.A
                    },
                    SubresourceRange = VulkanUtilities.Init<ImageSubresourceRange>() with
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };

                ImageView view;
                api.CreateImageView(mDevice.Device, &viewInfo, null, &view).Assert();

                framebuffers[i] = new VulkanSwapchainFramebuffer
                {
                    Image = viewInfo.Image,
                    View = view,
                    Framebuffer = new VulkanFramebuffer(mDevice, new VulkanFramebufferInfo
                    {
                        Size = Size,
                        RenderPass = mRenderPass,
                        Attachments = new ImageView[] { view } // todo: add depth view
                    })
                };
            }

            return framebuffers;
        }

        private unsafe void DestroyFramebuffers()
        {
            var api = VulkanContext.API;
            for (int i = 0; i < mFramebuffers.Length; i++)
            {
                var framebuffer = mFramebuffers[i];
                framebuffer.Framebuffer.Dispose();
                api.DestroyImageView(mDevice.Device, framebuffer.View, null);
            }
        }

        public void AcquireImage()
        {
            throw new NotImplementedException();
        }

        public void Present(ICommandQueue commandQueue, ICommandList commandList)
        {
            throw new NotImplementedException();
        }

        public IRenderTarget RenderTarget => mRenderPass;
        public IFramebuffer CurrentFramebuffer => throw new NotImplementedException();

        public bool VSync
        {
            get => mVSync;
            set
            {
                mVSync = value;
                Invalidate();
            }
        }

        public Vector2D<int> Size => new Vector2D<int>
        {
            X = (int)mExtent.Width,
            Y = (int)mExtent.Height
        };

        public event Action<Vector2D<int>>? SwapchainInvalidated;

        private readonly int mPresentQueueFamily;
        private readonly VulkanQueue mPresentQueue;
        private readonly VulkanDevice mDevice;
        private readonly Instance mInstance;
        private readonly IWindow mWindow;

        private readonly SurfaceKHR mSurface;
        private SwapchainKHR mSwapchain;
        private VulkanRenderPass mRenderPass;

        private VulkanSwapchainFramebuffer[] mFramebuffers;
        private Format mImageFormat;
        private Extent2D mExtent;

        private readonly KhrSwapchain mSwapchainExtension;
        private readonly KhrSurface mSurfaceExtension;
        private bool mDisposed, mVSync;
    }
}