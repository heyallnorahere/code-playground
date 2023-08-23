using Silk.NET.Vulkan;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using VMASharp;

using GPUQueueType = Optick.NET.GPUQueueType;
using OptickMacros = Optick.NET.OptickMacros;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct BoundPipelineData
    {
        public Dictionary<int, BoundPipelineDescriptorSet> Sets { get; set; }
        public VulkanPipeline Pipeline { get; set; }
        public HashSet<nint> DynamicIDs { get; set; }
    }

    internal struct BoundPipelineDescriptorSet
    {
        public Dictionary<int, BoundPipelineBinding> Bindings { get; set; }
    }

    internal struct BoundPipelineBinding
    {
        public HashSet<int> BoundIndices { get; set; }
    }

    public abstract class BindableVulkanImage : IBindableVulkanResource
    {
        public BindableVulkanImage()
        {
            mID = VulkanPipeline.GenerateID();
            mBoundPipelines = new Dictionary<ulong, BoundPipelineData>();
        }

        void IBindableVulkanResource.Bind(DescriptorSet[] sets, int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
            using var bindEvent = OptickMacros.Event();

            AddBindingIndex(set, binding, index, pipeline, dynamicId);
            BindSets(sets, (uint)binding, (uint)index);
        }

        private void AddBindingIndex(int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
            using var addBindingIndexEvent = OptickMacros.Event();

            ulong id = pipeline.ID;
            if (!mBoundPipelines.TryGetValue(id, out BoundPipelineData pipelineData))
            {
                mBoundPipelines.Add(id, pipelineData = new BoundPipelineData
                {
                    Sets = new Dictionary<int, BoundPipelineDescriptorSet>(),
                    Pipeline = pipeline,
                    DynamicIDs = new HashSet<nint>()
                });
            }

            if (dynamicId >= 0)
            {
                pipelineData.DynamicIDs.Add(dynamicId);
                return;
            }

            if (!pipelineData.Sets.TryGetValue(set, out BoundPipelineDescriptorSet setData))
            {
                pipelineData.Sets.Add(set, setData = new BoundPipelineDescriptorSet
                {
                    Bindings = new Dictionary<int, BoundPipelineBinding>()
                });
            }

            if (!setData.Bindings.TryGetValue(binding, out BoundPipelineBinding bindingData))
            {
                setData.Bindings.Add(binding, bindingData = new BoundPipelineBinding
                {
                    BoundIndices = new HashSet<int>()
                });
            }

            bindingData.BoundIndices.Add(index);
        }

        void IBindableVulkanResource.Unbind(int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
            using var unbindEvent = OptickMacros.Event();

            ulong id = pipeline.ID;
            if (!mBoundPipelines.TryGetValue(id, out BoundPipelineData pipelineData))
            {
                return;
            }

            if (dynamicId >= 0)
            {
                if (pipelineData.Sets.Count == 0 && pipelineData.DynamicIDs.Count == 1 && pipelineData.DynamicIDs.Contains(dynamicId))
                {
                    mBoundPipelines.Remove(id);
                    return;
                }

                pipelineData.DynamicIDs.Remove(dynamicId);
                return;
            }

            if (!pipelineData.Sets.TryGetValue(set, out BoundPipelineDescriptorSet setData))
            {
                return;
            }

            if (!setData.Bindings.TryGetValue(binding, out BoundPipelineBinding bindingData))
            {
                return;
            }

            if (bindingData.BoundIndices.Count == 1 && bindingData.BoundIndices.Contains(index))
            {
                if (setData.Bindings.Count == 1 && setData.Bindings.ContainsKey(binding))
                {
                    if (pipelineData.Sets.Count == 1 && pipelineData.Sets.ContainsKey(set) && pipelineData.DynamicIDs.Count == 0)
                    {
                        mBoundPipelines.Remove(id);
                        return;
                    }

                    pipelineData.Sets.Remove(set);
                    return;
                }

                setData.Bindings.Remove(binding);
                return;
            }

            bindingData.BoundIndices.Remove(index);
        }

        protected abstract void BindSets(DescriptorSet[] sets, uint binding, uint arrayElement);
        protected void Rebind()
        {
            using var rebindEvent = OptickMacros.Event();
            foreach (var pipelineId in mBoundPipelines.Keys)
            {
                var pipelineData = mBoundPipelines[pipelineId];
                var pipeline = pipelineData.Pipeline;

                foreach (int set in pipelineData.Sets.Keys)
                {
                    var setData = pipelineData.Sets[set];
                    foreach (int binding in setData.Bindings.Keys)
                    {
                        var bindingData = setData.Bindings[binding];
                        foreach (int index in bindingData.BoundIndices)
                        {
                            pipeline.Bind(this, set, binding, index);
                        }
                    }
                }

                foreach (nint dynamicId in pipelineData.DynamicIDs)
                {
                    pipeline.UpdateDynamicID(dynamicId);
                }
            }
        }

        protected void DestroyDynamicIDs()
        {
            using var destroyEvent = OptickMacros.Event();
            foreach (var pipelineData in mBoundPipelines.Values)
            {
                foreach (nint id in pipelineData.DynamicIDs)
                {
                    pipelineData.Pipeline.DestroyDynamicID(id);
                }
            }
        }

        ulong IBindableVulkanResource.ID => mID;

        private readonly ulong mID;
        private readonly Dictionary<ulong, BoundPipelineData> mBoundPipelines;
    }

    public struct VulkanImageLayout
    {
        public ImageLayout Layout { get; set; }
        public PipelineStageFlags Stage { get; set; }
        public AccessFlags AccessMask { get; set; }
    }

    public struct VulkanImageCreateInfo
    {
        public Size Size { get; set; }
        public DeviceImageUsageFlags Usage { get; set; }
        public int MipLevels { get; set; }
        public DeviceImageFormat Format { get; set; }
        public Format VulkanFormat { get; set; }
        public ImageAspectFlags AspectMask { get; set; }
        public ImageTiling Tiling { get; set; }
    }

    public sealed class VulkanImage : BindableVulkanImage, IDeviceImage
    {
        private static readonly Format[] sPreferredDepthFormats;
        static VulkanImage()
        {
            sPreferredDepthFormats = new Format[]
            {
                Format.D32SfloatS8Uint,
                Format.D32Sfloat,
                Format.D24UnormS8Uint
            };
        }

        public static Format FindSupportedDepthFormat(VulkanPhysicalDevice device, ImageTiling tiling)
        {
            if (!device.FindSupportedFormat(sPreferredDepthFormats, tiling, FormatFeatureFlags.DepthStencilAttachmentBit, out Format format))
            {
                throw new InvalidOperationException("Unable to find a suitable depth format!");
            }

            return format;
        }

        private static ImageUsageFlags ConvertUsageFlags(DeviceImageUsageFlags flags)
        {
            var values = flags.SplitFlags();
            ImageUsageFlags result = 0;

            foreach (var value in values)
            {
                result |= value switch
                {
                    DeviceImageUsageFlags.Render => ImageUsageFlags.SampledBit,
                    DeviceImageUsageFlags.ColorAttachment => ImageUsageFlags.ColorAttachmentBit,
                    DeviceImageUsageFlags.DepthStencilAttachment => ImageUsageFlags.DepthStencilAttachmentBit,
                    DeviceImageUsageFlags.CopySource => ImageUsageFlags.TransferSrcBit,
                    DeviceImageUsageFlags.CopyDestination => ImageUsageFlags.TransferDstBit,
                    DeviceImageUsageFlags.Storage => ImageUsageFlags.StorageBit,
                    _ => 0
                };
            }

            return result;
        }

        public VulkanImage(VulkanDevice device, VulkanMemoryAllocator allocator, VulkanImageCreateInfo info)
        {
            using var constructorEvent = OptickMacros.Event();

            Usage = info.Usage;
            Size = info.Size;
            ImageFormat = info.Format;
            Tiling = info.Tiling;
            Layout = GetLayout(DeviceImageLayoutName.Undefined);

            if (info.VulkanFormat != Format.Undefined)
            {
                VulkanFormat = info.VulkanFormat;
            }
            else
            {
                VulkanFormat = info.Format switch
                {
                    DeviceImageFormat.RGBA8_SRGB => Format.R8G8B8A8Srgb,
                    DeviceImageFormat.RGBA8_UNORM => Format.R8G8B8A8Unorm,
                    DeviceImageFormat.RGB8_SRGB => Format.R8G8B8Srgb,
                    DeviceImageFormat.RGB8_UNORM => Format.R8G8B8Unorm,
                    DeviceImageFormat.DepthStencil => FindSupportedDepthFormat(device.PhysicalDevice, info.Tiling),
                    _ => throw new ArgumentException("Invalid image format!")
                };
            }

            var physicalDevice = device.PhysicalDevice;
            physicalDevice.GetFormatProperties(VulkanFormat, out FormatProperties formatProperties);

            var tilingFeatureFlags = info.Tiling switch
            {
                ImageTiling.Optimal => formatProperties.OptimalTilingFeatures,
                ImageTiling.Linear => formatProperties.LinearTilingFeatures,
                _ => throw new ArgumentException("Unsupported image tiling!")
            };

            bool linearFilterSupported = tilingFeatureFlags.HasFlag(FormatFeatureFlags.SampledImageFilterLinearBit);
            mMipmapBlitFilter = linearFilterSupported ? Filter.Linear : Filter.Nearest;

            if (info.MipLevels > 0)
            {
                MipLevels = info.MipLevels;
            }
            else if ((info.Usage & (DeviceImageUsageFlags.ColorAttachment | DeviceImageUsageFlags.DepthStencilAttachment)) == 0)
            {
                MipLevels = (int)Math.Floor(Math.Log(Math.Max(info.Size.Width, info.Size.Height), 2)) + 1;
            }
            else
            {
                MipLevels = 1;
            }

            if (info.AspectMask != 0)
            {
                AspectMask = info.AspectMask;
            }
            else if (info.Format != DeviceImageFormat.DepthStencil)
            {
                AspectMask = ImageAspectFlags.ColorBit;
            }
            else
            {
                AspectMask = ImageAspectFlags.DepthBit;
                if (VulkanFormat.HasStencil())
                {
                    AspectMask |= ImageAspectFlags.StencilBit;
                }
            }

            mDevice = device;
            mAllocator = allocator;

            CreateImage();
            AllocateMemory();
            CreateView();
        }

        ~VulkanImage()
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
            DestroyDynamicIDs();

            var api = VulkanContext.API;
            api.DestroyImageView(mDevice.Device, mView, null);
            api.DestroyImage(mDevice.Device, mImage, null);

            mAllocator.FreeMemory(mAllocation);
        }

        private unsafe void CreateImage()
        {
            using var createEvent = OptickMacros.Event();
            var createInfo = VulkanUtilities.Init<ImageCreateInfo>() with
            {
                Flags = ImageCreateFlags.None,
                ImageType = ImageType.Type2D,
                Format = VulkanFormat,
                Extent = new Extent3D
                {
                    Width = (uint)Size.Width,
                    Height = (uint)Size.Height,
                    Depth = 1
                },
                MipLevels = (uint)MipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = Tiling,
                Usage = ConvertUsageFlags(Usage),
                InitialLayout = mLayout.Layout
            };

            var physicalDevice = mDevice.PhysicalDevice;
            var sharingMode = physicalDevice.FindSharingMode(out uint[]? indices, out uint indexCount);

            fixed (uint* indexPtr = indices)
            {
                createInfo.SharingMode = sharingMode;
                createInfo.QueueFamilyIndexCount = indexCount;
                createInfo.PQueueFamilyIndices = indexPtr;

                fixed (Silk.NET.Vulkan.Image* image = &mImage)
                {
                    var api = VulkanContext.API;
                    api.CreateImage(mDevice.Device, &createInfo, null, image).Assert();
                }
            }
        }

        private unsafe void CreateView()
        {
            using var createEvent = OptickMacros.Event();
            var createInfo = VulkanUtilities.Init<ImageViewCreateInfo>() with
            {
                Flags = ImageViewCreateFlags.None,
                Image = mImage,
                ViewType = ImageViewType.Type2D,
                Format = VulkanFormat,
                SubresourceRange = VulkanUtilities.Init<ImageSubresourceRange>() with
                {
                    AspectMask = AspectMask,
                    BaseMipLevel = 0,
                    LevelCount = (uint)MipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            fixed (ImageView* view = &mView)
            {
                var api = VulkanContext.API;
                api.CreateImageView(mDevice.Device, &createInfo, null, view).Assert();
            }

            mImageInfo = VulkanUtilities.Init<DescriptorImageInfo>() with
            {
                Sampler = default,
                ImageLayout = GetLayout(DeviceImageLayoutName.ComputeStorage).Layout, // we don't need any other layouts - vulkan's gonna get angry anyway
                ImageView = mView
            };
        }

        [MemberNotNull(nameof(mAllocation))]
        private void AllocateMemory()
        {
            using var allocateEvent = OptickMacros.Event();
            mAllocation = mAllocator.AllocateMemoryForImage(mImage, new AllocationCreateInfo
            {
                Usage = MemoryUsage.GPU_Only
            }, false);

            mAllocation.BindImageMemory(mImage);
        }

        object IDeviceImage.GetLayout(DeviceImageLayoutName name) => GetLayout(name);
        public static VulkanImageLayout GetLayout(DeviceImageLayoutName name)
        {
            using var getLayoutEvent = OptickMacros.Event();
            return name switch
            {
                DeviceImageLayoutName.Undefined => new VulkanImageLayout
                {
                    Layout = ImageLayout.Undefined,
                    Stage = PipelineStageFlags.TopOfPipeBit,
                    AccessMask = AccessFlags.None
                },
                DeviceImageLayoutName.ShaderReadOnly => new VulkanImageLayout
                {
                    Layout = ImageLayout.ShaderReadOnlyOptimal,
                    Stage = PipelineStageFlags.FragmentShaderBit,
                    AccessMask = AccessFlags.ShaderReadBit
                },
                DeviceImageLayoutName.ColorAttachment => new VulkanImageLayout
                {
                    Layout = ImageLayout.ColorAttachmentOptimal,
                    Stage = PipelineStageFlags.ColorAttachmentOutputBit,
                    AccessMask = AccessFlags.ColorAttachmentWriteBit
                },
                DeviceImageLayoutName.DepthStencilAttachment => new VulkanImageLayout
                {
                    Layout = ImageLayout.DepthStencilAttachmentOptimal,
                    Stage = PipelineStageFlags.EarlyFragmentTestsBit,
                    AccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
                },
                DeviceImageLayoutName.CopySource => new VulkanImageLayout
                {
                    Layout = ImageLayout.TransferSrcOptimal,
                    Stage = PipelineStageFlags.TransferBit,
                    AccessMask = AccessFlags.TransferReadBit
                },
                DeviceImageLayoutName.CopyDestination => new VulkanImageLayout
                {
                    Layout = ImageLayout.TransferDstOptimal,
                    Stage = PipelineStageFlags.TransferBit,
                    AccessMask = AccessFlags.TransferWriteBit
                },
                DeviceImageLayoutName.ComputeStorage => new VulkanImageLayout
                {
                    Layout = ImageLayout.General,
                    Stage = PipelineStageFlags.ComputeShaderBit,
                    AccessMask = AccessFlags.ShaderWriteBit
                },
                _ => throw new ArgumentException("Invalid image layout name!")
            };
        }

        public void Load<T>(Image<T> image) where T : unmanaged, IPixel<T>
        {
            using var loadEvent = OptickMacros.Event();

            var data = new T[Size.Width * Size.Height];
            image.CopyPixelDataTo(data);

            Load(data);
        }

        public unsafe void Load<T>(T[] data) where T : unmanaged
        {
            using var loadEvent = OptickMacros.Event();

            using var stagingBuffer = new VulkanBuffer(mDevice, mAllocator, DeviceBufferUsage.Staging, data.Length * sizeof(T));
            fixed (T* ptr = data)
            {
                stagingBuffer.CopyFromCPU(ptr, stagingBuffer.Size);
            }

            var queue = mDevice.GetQueue(CommandQueueFlags.Transfer);
            var commandBuffer = queue.Release();
            commandBuffer.Begin();

            using (commandBuffer.Context(GPUQueueType.Transfer))
            {
                CopyFromBuffer(commandBuffer, stagingBuffer, Layout);
            }

            commandBuffer.End();
            queue.Submit(commandBuffer, wait: true);
        }

        void IDeviceImage.CopyFromBuffer(ICommandList commandList, IDeviceBuffer source, object currentLayout)
        {
            if (commandList is not VulkanCommandBuffer || source is not VulkanBuffer || currentLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyFromBuffer((VulkanCommandBuffer)commandList, (VulkanBuffer)source, (VulkanImageLayout)currentLayout);
        }

        public void CopyFromBuffer(VulkanCommandBuffer commandBuffer, VulkanBuffer source, VulkanImageLayout currentLayout)
        {
            using var copyEvent = OptickMacros.Event();
            using var gpuCopyEvent = OptickMacros.GPUEvent("Buffer-to-image copy");

            var api = VulkanContext.API;
            var transferDst = GetLayout(DeviceImageLayoutName.CopyDestination);
            var transferSrc = GetLayout(DeviceImageLayoutName.CopySource);

            var copyRegion = VulkanUtilities.Init<BufferImageCopy>() with
            {
                BufferOffset = 0,
                BufferImageHeight = 0,
                BufferRowLength = 0,
                ImageSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                {
                    AspectMask = AspectMask,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageOffset = VulkanUtilities.Init<Offset3D>(),
                ImageExtent = new Extent3D
                {
                    Width = (uint)Size.Width,
                    Height = (uint)Size.Height,
                    Depth = 1
                }
            };

            TransitionLayout(commandBuffer, currentLayout, transferDst, 0, MipLevels);
            api.CmdCopyBufferToImage(commandBuffer.Buffer, source.Buffer, mImage, transferDst.Layout, 1, copyRegion);

            if (MipLevels > 1)
            {
                int currentWidth = Size.Width;
                int currentHeight = Size.Height;
                for (int i = 1; i < MipLevels; i++)
                {
                    int nextWidth = currentWidth > 1 ? currentWidth / 2 : 1;
                    int nextHeight = currentHeight > 1 ? currentHeight / 2 : 1;

                    var blitRegion = VulkanUtilities.Init<ImageBlit>() with
                    {
                        SrcSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                        {
                            AspectMask = AspectMask,
                            MipLevel = (uint)i - 1,
                            BaseArrayLayer = 0,
                            LayerCount = 1
                        },
                        DstSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                        {
                            AspectMask = AspectMask,
                            MipLevel = (uint)i,
                            BaseArrayLayer = 0,
                            LayerCount = 1
                        }
                    };

                    blitRegion.SrcOffsets.Element1 = new Offset3D
                    {
                        X = currentWidth,
                        Y = currentHeight,
                        Z = 1
                    };

                    blitRegion.DstOffsets.Element1 = new Offset3D
                    {
                        X = nextWidth,
                        Y = nextHeight,
                        Z = 1
                    };

                    TransitionLayout(commandBuffer, transferDst, transferSrc, i - 1, 1);
                    api.CmdBlitImage(commandBuffer.Buffer, mImage, transferSrc.Layout, mImage, transferDst.Layout, 1, blitRegion, mMipmapBlitFilter);

                    currentWidth = nextWidth;
                    currentHeight = nextHeight;
                }

                TransitionLayout(commandBuffer, transferSrc, currentLayout, 0, MipLevels - 1);
            }

            TransitionLayout(commandBuffer, transferDst, currentLayout, MipLevels - 1, 1);
        }

        void IDeviceImage.CopyToBuffer(ICommandList commandList, IDeviceBuffer destination, object currentLayout)
        {
            if (commandList is not VulkanCommandBuffer || destination is not VulkanBuffer || currentLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyToBuffer((VulkanCommandBuffer)commandList, (VulkanBuffer)destination, (VulkanImageLayout)currentLayout);
        }

        public void CopyToBuffer(VulkanCommandBuffer commandBuffer, VulkanBuffer destination, VulkanImageLayout currentLayout)
        {
            using var copyEvent = OptickMacros.Event();
            using var gpuCopyEvent = OptickMacros.GPUEvent("Image-to-buffer copy");

            var api = VulkanContext.API;
            var transferDst = GetLayout(DeviceImageLayoutName.CopySource);

            var copyRegion = VulkanUtilities.Init<BufferImageCopy>() with
            {
                BufferOffset = 0,
                BufferImageHeight = 0,
                BufferRowLength = 0,
                ImageSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                {
                    AspectMask = AspectMask,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageOffset = VulkanUtilities.Init<Offset3D>(),
                ImageExtent = new Extent3D
                {
                    Width = (uint)Size.Width,
                    Height = (uint)Size.Height,
                    Depth = 1
                }
            };

            TransitionLayout(commandBuffer, currentLayout, transferDst, 0, 1);
            api.CmdCopyImageToBuffer(commandBuffer.Buffer, mImage, transferDst.Layout, destination.Buffer, 1, copyRegion);
            TransitionLayout(commandBuffer, transferDst, currentLayout, 0, 1);
        }

        void IDeviceImage.TransitionLayout(ICommandList commandList, object srcLayout, object dstLayout)
        {
            if (commandList is not VulkanCommandBuffer || srcLayout is not VulkanImageLayout || dstLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            TransitionLayout((VulkanCommandBuffer)commandList, (VulkanImageLayout)srcLayout, (VulkanImageLayout)dstLayout, 0, MipLevels);
        }

        public unsafe void TransitionLayout(VulkanCommandBuffer commandBuffer, VulkanImageLayout sourceLayout, VulkanImageLayout destinationLayout, int baseMipLevel, int levelCount)
        {
            using var transitionEvent = OptickMacros.Event();
            using var barrierEvent = OptickMacros.GPUEvent("Image pipeline barrier");

            var barrier = VulkanUtilities.Init<ImageMemoryBarrier>() with
            {
                SrcAccessMask = sourceLayout.AccessMask,
                DstAccessMask = destinationLayout.AccessMask,
                OldLayout = sourceLayout.Layout,
                NewLayout = destinationLayout.Layout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = mImage,
                SubresourceRange = VulkanUtilities.Init<ImageSubresourceRange>() with
                {
                    AspectMask = AspectMask,
                    BaseMipLevel = (uint)baseMipLevel,
                    LevelCount = (uint)levelCount,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var api = VulkanContext.API;
            api.CmdPipelineBarrier(commandBuffer.Buffer, sourceLayout.Stage, destinationLayout.Stage,
                                   DependencyFlags.None, 0, null, 0, null, 1, &barrier);
        }

        ITexture IDeviceImage.CreateTexture(bool ownsImage, ISamplerSettings? samplerSettings)
        {
            return new VulkanTexture(this, ownsImage, samplerSettings);
        }

        protected override unsafe void BindSets(DescriptorSet[] sets, uint binding, uint arrayElement)
        {
            using var bindEvent = OptickMacros.Event();
            fixed (DescriptorImageInfo* imageInfo = &mImageInfo)
            {
                var writes = new WriteDescriptorSet[sets.Length];
                for (int i = 0; i < writes.Length; i++)
                {
                    writes[i] = VulkanUtilities.Init<WriteDescriptorSet>() with
                    {
                        DstSet = sets[i],
                        DstBinding = binding,
                        DstArrayElement = arrayElement,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.StorageImage, // create a texture for sampling
                        PImageInfo = imageInfo
                    };
                }

                var api = VulkanContext.API;
                api.UpdateDescriptorSets(mDevice.Device, writes, 0, null);
            }
        }

        public DeviceImageUsageFlags Usage { get; }
        public Size Size { get; }
        public int MipLevels { get; }
        public DeviceImageFormat ImageFormat { get; }
        public Format VulkanFormat { get; }
        public ImageAspectFlags AspectMask { get; }
        public ImageTiling Tiling { get; }
        public ImageView View => mView;

        public VulkanImageLayout Layout
        {
            get => mLayout;
            set
            {
                mLayout = value;
                OnLayoutChanged?.Invoke(value);
            }
        }

        public event Action<VulkanImageLayout>? OnLayoutChanged;
        internal VulkanDevice Device => mDevice;

        object IDeviceImage.Layout
        {
            get => Layout;
            set => Layout = (VulkanImageLayout)value;
        }

        private readonly VulkanDevice mDevice;
        private readonly VulkanMemoryAllocator mAllocator;

        private Silk.NET.Vulkan.Image mImage;
        private ImageView mView;
        private DescriptorImageInfo mImageInfo;
        private Allocation mAllocation;
        private VulkanImageLayout mLayout;

        private readonly Filter mMipmapBlitFilter;
        private bool mDisposed;
    }
}