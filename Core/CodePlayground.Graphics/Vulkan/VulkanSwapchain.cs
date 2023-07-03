using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using VMASharp;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanSwapchainFramebuffer
    {
        public Image Image { get; set; }
        public ImageView View { get; set; }
        public VulkanFramebuffer Framebuffer { get; set; }
    }

    internal struct VulkanSwapchainFrameSyncObjects
    {
        public Fence Fence;
        public Semaphore ImageAvailable;
        public Semaphore RenderFinished;
    }

    public sealed class VulkanSwapchain : ISwapchain, IDisposable
    {
        private const ImageTiling DepthBufferImageTiling = ImageTiling.Optimal;

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

        internal VulkanSwapchain(SurfaceKHR surface, VulkanContext context, Instance instance, IWindow window)
        {
            mDevice = context.Device;
            mAllocator = context.Allocator;
            mInstance = instance;
            mWindow = window;

            var api = VulkanContext.API;
            mSwapchainExtension = api.GetDeviceExtension<KhrSwapchain>(instance, mDevice.Device);
            mSurfaceExtension = api.GetInstanceExtension<KhrSurface>(instance);
            mDisposed = false;
            mVSync = false;

            mDepthFormat = VulkanImage.FindSupportedDepthFormat(mDevice.PhysicalDevice, DepthBufferImageTiling);
            mPresentQueueFamily = FindPresentQueueFamily(mSurfaceExtension, mDevice.PhysicalDevice, surface);
            mPresentQueue = mDevice.GetQueue(mPresentQueueFamily);

            mCurrentImage = 0;
            mCurrentSyncFrame = 0;

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

            var api = VulkanContext.API;
            foreach (var syncFrame in mSyncObjects)
            {
                api.DestroyFence(mDevice.Device, syncFrame.Fence, null);
                api.DestroySemaphore(mDevice.Device, syncFrame.ImageAvailable, null);
                api.DestroySemaphore(mDevice.Device, syncFrame.RenderFinished, null);
            }

            DestroyFramebuffers();
            mDepthBuffer.Dispose();
            mRenderPass.Dispose();

            mSwapchainExtension.DestroySwapchain(mDevice.Device, mSwapchain, null);
            mSurfaceExtension.DestroySurface(mInstance, mSurface, null);
        }

        private void Resize(Vector2D<int> windowSize) => mNewSize = windowSize;
        public unsafe void Invalidate()
        {
            mDevice.ClearQueues();

            DestroyFramebuffers();
            mDepthBuffer.Dispose();

            if (mNewSize is not null)
            {
                mExtent.Width = (uint)mNewSize.Value.X;
                mExtent.Height = (uint)mNewSize.Value.Y;
            }

            var old = Create(mExtent);
            if (old.Handle != 0)
            {
                mSwapchainExtension.DestroySwapchain(mDevice.Device, old, null);
            }

            mCurrentImage = 0;
            SwapchainInvalidated?.Invoke(Width, Height);
        }

        private SurfaceFormatKHR ChooseSurfaceFormat()
        {
            var formats = QuerySurfaceFormats(mSurfaceExtension, mDevice.PhysicalDevice, mSurface);
            foreach (var format in formats)
            {
                if (format.Format == Format.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
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

        private static Extent2D ChooseExtent(Extent2D passedExtent, SurfaceCapabilitiesKHR capabilities)
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

        [MemberNotNull(nameof(mRenderPass))]
        [MemberNotNull(nameof(mFramebuffers))]
        [MemberNotNull(nameof(mSyncObjects))]
        [MemberNotNull(nameof(mDepthBuffer))]
        [MemberNotNull(nameof(mImageFences))]
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

            var depthStencilLayout = VulkanImage.GetLayout(DeviceImageLayoutName.DepthStencilAttachment).Layout;
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
                DepthAttachment = new VulkanRenderPassAttachment
                {
                    ImageFormat = mDepthFormat,
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.DontCare,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.Undefined,
                    FinalLayout = depthStencilLayout,
                    Layout = depthStencilLayout
                },
                SubpassDependency = new VulkanSubpassDependency
                {
                    SourceStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                    SourceAccessMask = 0,
                    DestinationStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                    DestinationAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
                }
            });

            mSyncObjects ??= CreateSyncObjects();
            mDepthBuffer = CreateDepthBuffer();
            mFramebuffers = CreateFramebuffers();

            mImageFences = new Fence[mFramebuffers.Length];
            Array.Fill(mImageFences, default);

            return old;
        }

        private VulkanImage CreateDepthBuffer()
        {
            var image = new VulkanImage(mDevice, mAllocator, new VulkanImageCreateInfo
            {
                Size = new SixLabors.ImageSharp.Size((int)mExtent.Width, (int)mExtent.Height),
                Usage = DeviceImageUsageFlags.DepthStencilAttachment,
                MipLevels = 1,
                Format = DeviceImageFormat.DepthStencil,
                VulkanFormat = mDepthFormat,
                Tiling = DepthBufferImageTiling
            });

            var queue = mDevice.GetQueue(CommandQueueFlags.Transfer);
            var commandBuffer = queue.Release();
            commandBuffer.Begin();

            var newLayout = VulkanImage.GetLayout(DeviceImageLayoutName.DepthStencilAttachment);
            image.TransitionLayout(commandBuffer, image.Layout, newLayout, 0, 1);

            commandBuffer.End();
            queue.Submit(commandBuffer, wait: true);

            image.Layout = newLayout;
            return image;
        }

        private unsafe VulkanSwapchainFrameSyncObjects[] CreateSyncObjects()
        {
            var result = new VulkanSwapchainFrameSyncObjects[2]; // 2 frames, hardcoded constant

            var semaphoreInfo = VulkanUtilities.Init<SemaphoreCreateInfo>();
            var fenceInfo = VulkanUtilities.Init<FenceCreateInfo>() with
            {
                Flags = FenceCreateFlags.SignaledBit
            };

            var api = VulkanContext.API;
            for (int i = 0; i < result.Length; i++)
            {
                var frame = new VulkanSwapchainFrameSyncObjects();

                api.CreateFence(mDevice.Device, &fenceInfo, null, &frame.Fence).Assert();
                api.CreateSemaphore(mDevice.Device, &semaphoreInfo, null, &frame.ImageAvailable).Assert();
                api.CreateSemaphore(mDevice.Device, &semaphoreInfo, null, &frame.RenderFinished).Assert();

                result[i] = frame;
            }

            return result;
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
                        Width = Width,
                        Height = Height,
                        RenderPass = mRenderPass,
                        Attachments = new ImageView[]
                        {
                            view,
                            mDepthBuffer.View
                        }
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

        public unsafe void AcquireImage()
        {
            var api = VulkanContext.API;
            var currentFrame = mSyncObjects[mCurrentSyncFrame];

            var fence = currentFrame.Fence;
            api.WaitForFences(mDevice.Device, 1, fence, true, ulong.MaxValue).Assert();

            while (true)
            {
                fixed (uint* currentImage = &mCurrentImage)
                {
                    var result = mSwapchainExtension.AcquireNextImage(mDevice.Device, mSwapchain,
                                                                      ulong.MaxValue, currentFrame.ImageAvailable,
                                                                      new Fence(null), currentImage);

                    if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
                    {
                        Invalidate();
                        continue;
                    }
                    else
                    {
                        result.Assert();
                    }
                }

                break;
            }

            if (mImageFences[mCurrentImage].Handle != 0)
            {
                api.WaitForFences(mDevice.Device, 1, mImageFences[mCurrentImage], true, ulong.MaxValue).Assert();
            }

            mImageFences[mCurrentImage] = fence;
        }

        public unsafe void Present(ICommandQueue commandQueue, ICommandList commandList)
        {
            if (commandQueue is not VulkanQueue || commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            var queue = (VulkanQueue)commandQueue;
            var commandBuffer = (VulkanCommandBuffer)commandList;

            var syncFrame = mSyncObjects[mCurrentSyncFrame];
            var fence = syncFrame.Fence;
            var api = VulkanContext.API;
            api.ResetFences(mDevice.Device, 1, fence).Assert();

            queue.Submit(commandBuffer, new VulkanQueueSubmitInfo
            {
                Fence = fence,
                WaitSemaphores = new VulkanQueueSemaphoreDependency[]
                {
                    new VulkanQueueSemaphoreDependency
                    {
                        Semaphore = syncFrame.ImageAvailable,
                        DestinationStageMask = PipelineStageFlags.ColorAttachmentOutputBit
                    }
                },
                SignalSemaphores = new Semaphore[] { syncFrame.RenderFinished }
            });

            fixed (SwapchainKHR* swapchain = &mSwapchain)
            {
                fixed (uint* currentImage = &mCurrentImage)
                {
                    var presentInfo = VulkanUtilities.Init<PresentInfoKHR>() with
                    {
                        WaitSemaphoreCount = 1,
                        PWaitSemaphores = &syncFrame.RenderFinished,
                        SwapchainCount = 1,
                        PSwapchains = swapchain,
                        PImageIndices = currentImage
                    };

                    var result = mSwapchainExtension.QueuePresent(mPresentQueue.Queue, &presentInfo);
                    if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || mNewSize is not null)
                    {
                        Invalidate();
                        mNewSize = null;
                    }
                    else
                    {
                        result.Assert();
                    }
                }
            }

            mCurrentSyncFrame = (mCurrentSyncFrame + 1) % mSyncObjects.Length;
        }

        public IRenderTarget RenderTarget => mRenderPass;
        public IFramebuffer CurrentFramebuffer => mFramebuffers[mCurrentImage].Framebuffer;
        public int CurrentFrame => (int)mCurrentImage;
        public int FrameCount => mFramebuffers.Length;

        public bool VSync
        {
            get => mVSync;
            set
            {
                mVSync = value;
                Invalidate();
            }
        }

        public int Width => (int)mExtent.Width;
        public int Height => (int)mExtent.Height;

        public event Action<int, int>? SwapchainInvalidated;

        private readonly int mPresentQueueFamily;
        private readonly VulkanQueue mPresentQueue;
        private readonly VulkanDevice mDevice;
        private readonly VulkanMemoryAllocator mAllocator;
        private readonly Instance mInstance;
        private readonly IWindow mWindow;

        private readonly SurfaceKHR mSurface;
        private SwapchainKHR mSwapchain;
        private VulkanRenderPass mRenderPass;

        private VulkanSwapchainFrameSyncObjects[] mSyncObjects;
        private Fence[] mImageFences;
        private uint mCurrentImage;
        private int mCurrentSyncFrame;

        private VulkanSwapchainFramebuffer[] mFramebuffers;
        private Format mImageFormat;

        private VulkanImage mDepthBuffer;
        private readonly Format mDepthFormat;

        private Extent2D mExtent;
        private Vector2D<int>? mNewSize;

        private readonly KhrSwapchain mSwapchainExtension;
        private readonly KhrSurface mSurfaceExtension;
        private bool mDisposed, mVSync;
    }
}
