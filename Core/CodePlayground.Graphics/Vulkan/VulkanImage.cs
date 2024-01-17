using Silk.NET.Vulkan;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using VMASharp;

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
            using var bindEvent = Profiler.Event();

            AddBindingIndex(set, binding, index, pipeline, dynamicId);
            BindSets(sets, (uint)binding, (uint)index);
        }

        private void AddBindingIndex(int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
            using var addBindingIndexEvent = Profiler.Event();

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
            using var unbindEvent = Profiler.Event();

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
            using var rebindEvent = Profiler.Event();
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
            using var destroyEvent = Profiler.Event();
            foreach (var pipelineData in mBoundPipelines.Values)
            {
                foreach (nint id in pipelineData.DynamicIDs)
                {
                    pipelineData.Pipeline.DestroyDynamicID(id);
                }
            }
        }

        ulong IBindableVulkanResource.ID => mID;

        protected readonly ulong mID;
        private readonly Dictionary<ulong, BoundPipelineData> mBoundPipelines;
    }

    public struct VulkanImageLayout : IDeviceImageLayout
    {
        public ImageLayout Layout { get; set; }
        public PipelineStageFlags Stage { get; set; }
        public AccessFlags AccessMask { get; set; }
        public DeviceImageLayoutName Name { get; set; }
    }

    public struct VulkanImageCreateInfo
    {
        public Size Size { get; set; }
        public DeviceImageUsageFlags Usage { get; set; }
        public DeviceImageType ImageType { get; set; }
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
            using var constructorEvent = Profiler.Event();

            Usage = info.Usage;
            Type = info.ImageType;
            Size = info.Size;
            ImageFormat = info.Format;
            Tiling = info.Tiling;
            Layout = GetLayout(DeviceImageLayoutName.Undefined);

            ArrayLayers = Type switch
            {
                DeviceImageType.Type2D => 1,
                DeviceImageType.TypeCube => 6,
                _ => throw new ArgumentException("Invalid image type!")
            };

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
                    DeviceImageFormat.R8_UNORM => Format.R8Unorm,
                    DeviceImageFormat.R16_UNORM => Format.R16Unorm,
                    DeviceImageFormat.RG16_UNORM => Format.R16G16Unorm,
                    DeviceImageFormat.R32_SFLOAT => Format.R32Sfloat,
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
            using var disposeEvent = Profiler.Event();
            DestroyDynamicIDs();

            var api = VulkanContext.API;
            api.DestroyImageView(mDevice.Device, mView, null);
            api.DestroyImage(mDevice.Device, mImage, null);

            mAllocator.FreeMemory(mAllocation);
        }

        private unsafe void CreateImage()
        {
            using var createEvent = Profiler.Event();

            var flags = ImageCreateFlags.None;
            if (Type == DeviceImageType.TypeCube)
            {
                flags |= ImageCreateFlags.CreateCubeCompatibleBit;
            }

            var createInfo = VulkanUtilities.Init<ImageCreateInfo>() with
            {
                Flags = flags,
                ImageType = ImageType.Type2D,
                Format = VulkanFormat,
                Extent = new Extent3D
                {
                    Width = (uint)Size.Width,
                    Height = (uint)Size.Height,
                    Depth = 1
                },
                MipLevels = (uint)MipLevels,
                ArrayLayers = (uint)ArrayLayers,
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
            using var createEvent = Profiler.Event();
            var createInfo = VulkanUtilities.Init<ImageViewCreateInfo>() with
            {
                Flags = ImageViewCreateFlags.None,
                Image = mImage,
                ViewType = Type switch
                {
                    DeviceImageType.Type2D => ImageViewType.Type2D,
                    DeviceImageType.TypeCube => ImageViewType.TypeCube,
                    _ => throw new ArgumentException("Invalid image type!")
                },
                Format = VulkanFormat,
                SubresourceRange = VulkanUtilities.Init<ImageSubresourceRange>() with
                {
                    AspectMask = AspectMask & (ImageAspectFlags.ColorBit | ImageAspectFlags.DepthBit),
                    BaseMipLevel = 0,
                    LevelCount = (uint)MipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = (uint)ArrayLayers
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
            using var allocateEvent = Profiler.Event();
            mAllocation = mAllocator.AllocateMemoryForImage(mImage, new AllocationCreateInfo
            {
                Usage = MemoryUsage.GPU_Only
            }, false);

            mAllocation.BindImageMemory(mImage);
        }

        IDeviceImageLayout IDeviceImage.GetLayout(DeviceImageLayoutName name) => GetLayout(name);
        public static VulkanImageLayout GetLayout(DeviceImageLayoutName name)
        {
            using var getLayoutEvent = Profiler.Event();
            var layout = name switch
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

            layout.Name = name;
            return layout;
        }

        public void Load<T>(Image<T> image) where T : unmanaged, IPixel<T>
        {
            using var loadEvent = Profiler.Event();

            var data = new T[Size.Width * Size.Height];
            image.CopyPixelDataTo(data);

            Load(data);
        }

        public unsafe void Load<T>(T[] data) where T : unmanaged
        {
            using var loadEvent = Profiler.Event();

            using var stagingBuffer = new VulkanBuffer(mDevice, mAllocator, DeviceBufferUsage.Staging, data.Length * sizeof(T));
            fixed (T* ptr = data)
            {
                stagingBuffer.CopyFromCPU(ptr, stagingBuffer.Size);
            }

            var queue = mDevice.GetQueue(CommandQueueFlags.Transfer);
            var commandBuffer = queue.Release();
            commandBuffer.Begin();

            CopyFromBuffer(commandBuffer, stagingBuffer, Layout, 0, ArrayLayers);

            commandBuffer.End();
            queue.Submit(commandBuffer, wait: true);
        }

        void IDeviceImage.CopyFromBuffer(ICommandList commandList, IDeviceBuffer source, IDeviceImageLayout currentLayout)
        {
            if (commandList is not VulkanCommandBuffer || source is not VulkanBuffer || currentLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyFromBuffer((VulkanCommandBuffer)commandList, (VulkanBuffer)source, (VulkanImageLayout)currentLayout, 0, ArrayLayers);
        }

        void IDeviceImage.CopyFromBuffer(ICommandList commandList, IDeviceBuffer source, ImageSelection destination, IDeviceImageLayout currentLayout)
        {
            if (commandList is not VulkanCommandBuffer || source is not VulkanBuffer || currentLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyFromBuffer((VulkanCommandBuffer)commandList, (VulkanBuffer)source, destination, (VulkanImageLayout)currentLayout, 0, ArrayLayers);
        }

        public void CopyFromBuffer(VulkanCommandBuffer commandBuffer, VulkanBuffer source, VulkanImageLayout currentLayout, int arrayLayer, int layerCount)
        {
            CopyFromBuffer(commandBuffer, source, ImageSelection.Default, currentLayout, arrayLayer, layerCount);
        }

        public void CopyFromBuffer(VulkanCommandBuffer commandBuffer, VulkanBuffer source, ImageSelection destination, VulkanImageLayout currentLayout, int arrayLayer, int layerCount)
        {
            using var copyEvent = Profiler.GPUEvent(commandBuffer, "Copy buffer to image");

            var api = VulkanContext.API;
            var transferDst = GetLayout(DeviceImageLayoutName.CopyDestination);

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
                ImageOffset = new Offset3D
                {
                    X = destination.X,
                    Y = destination.Y,
                    Z = 0
                },
                ImageExtent = new Extent3D
                {
                    Width = (uint)(destination.Width < 0 ? Size.Width : destination.Width),
                    Height = (uint)(destination.Height < 0 ? Size.Height : destination.Height),
                    Depth = 1
                }
            };

            TransitionLayout(commandBuffer, currentLayout, transferDst, 0, MipLevels, arrayLayer, layerCount);
            api.CmdCopyBufferToImage(commandBuffer.Buffer, source.Buffer, mImage, transferDst.Layout, 1, copyRegion);

            GenerateMipmaps(commandBuffer, currentLayout, arrayLayer, layerCount);
            TransitionLayout(commandBuffer, transferDst, currentLayout, MipLevels - 1, 1, arrayLayer, layerCount);
        }

        /// <summary>
        /// assumes that every mip level is at CopyDestination.
        /// sets every mip level before the last to the initial layout (currentLayout).
        /// keeps the last at CopyDestination
        /// </summary>
        private void GenerateMipmaps(VulkanCommandBuffer commandBuffer, VulkanImageLayout currentLayout, int arrayLayer, int layerCount)
        {
            using var generateEvent = Profiler.GPUEvent(commandBuffer, "Generate mipmaps");

            if (MipLevels <= 1)
            {
                return;
            }

            var api = VulkanContext.API;
            var transferDst = GetLayout(DeviceImageLayoutName.CopyDestination);
            var transferSrc = GetLayout(DeviceImageLayoutName.CopySource);

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
                        BaseArrayLayer = (uint)arrayLayer,
                        LayerCount = (uint)layerCount,
                    },
                    DstSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                    {
                        AspectMask = AspectMask,
                        MipLevel = (uint)i,
                        BaseArrayLayer = (uint)arrayLayer,
                        LayerCount = (uint)layerCount
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

                TransitionLayout(commandBuffer, transferDst, transferSrc, i - 1, 1, arrayLayer, layerCount);
                api.CmdBlitImage(commandBuffer.Buffer, mImage, transferSrc.Layout, mImage, transferDst.Layout, 1, blitRegion, mMipmapBlitFilter);

                currentWidth = nextWidth;
                currentHeight = nextHeight;
            }

            TransitionLayout(commandBuffer, transferSrc, currentLayout, 0, MipLevels - 1, arrayLayer, layerCount);
        }

        void IDeviceImage.CopyToBuffer(ICommandList commandList, IDeviceBuffer destination, IDeviceImageLayout currentLayout)
        {
            if (commandList is not VulkanCommandBuffer || destination is not VulkanBuffer || currentLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyToBuffer((VulkanCommandBuffer)commandList, (VulkanBuffer)destination, (VulkanImageLayout)currentLayout, 0, ArrayLayers);
        }

        void IDeviceImage.CopyToBuffer(ICommandList commandList, ImageSelection source, IDeviceBuffer destination, IDeviceImageLayout currentLayout)
        {
            if (commandList is not VulkanCommandBuffer || destination is not VulkanBuffer || currentLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyToBuffer((VulkanCommandBuffer)commandList, source, (VulkanBuffer)destination, (VulkanImageLayout)currentLayout, 0, ArrayLayers);
        }

        public void CopyToBuffer(VulkanCommandBuffer commandBuffer, VulkanBuffer destination, VulkanImageLayout currentLayout, int arrayLayer, int layerCount)
        {
            CopyToBuffer(commandBuffer, ImageSelection.Default, destination, currentLayout, arrayLayer, layerCount);
        }

        public void CopyToBuffer(VulkanCommandBuffer commandBuffer, ImageSelection source, VulkanBuffer destination, VulkanImageLayout currentLayout, int arrayLayer, int layerCount)
        {
            using var copyEvent = Profiler.GPUEvent(commandBuffer, "Copy image to buffer");

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
                    BaseArrayLayer = (uint)arrayLayer,
                    LayerCount = (uint)layerCount
                },
                ImageOffset = new Offset3D
                {
                    X = source.X,
                    Y = source.Y,
                    Z = 0
                },
                ImageExtent = new Extent3D
                {
                    Width = (uint)(source.Width < 0 ? Size.Width : source.Width),
                    Height = (uint)(source.Height < 0 ? Size.Height : source.Height),
                    Depth = 1
                }
            };

            TransitionLayout(commandBuffer, currentLayout, transferDst, 0, 1, arrayLayer, layerCount);
            api.CmdCopyImageToBuffer(commandBuffer.Buffer, mImage, transferDst.Layout, destination.Buffer, 1, copyRegion);
            TransitionLayout(commandBuffer, transferDst, currentLayout, 0, 1, arrayLayer, layerCount);
        }

        void IDeviceImage.CopyToBuffer(ICommandList commandList, int bufferOffset, int pixelStride, IDeviceBuffer destination, IDeviceImageLayout currentLayout)
        {
            if (commandList is not VulkanCommandBuffer commandBuffer || destination is not VulkanBuffer destinationBuffer || currentLayout is not VulkanImageLayout layout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyToBuffer(commandBuffer, bufferOffset, pixelStride, destinationBuffer, layout, 0, ArrayLayers);
        }

        public void CopyToBuffer(VulkanCommandBuffer commandBuffer, int bufferOffset, int pixelStride, VulkanBuffer destination, VulkanImageLayout currentLayout, int arrayLayer, int layerCount)
        {
            using var copyEvent = Profiler.GPUEvent(commandBuffer, "Copy image to buffer");

            var api = VulkanContext.API;
            var transferDst = GetLayout(DeviceImageLayoutName.CopySource);

            var copyRegion = VulkanUtilities.Init<BufferImageCopy>() with
            {
                BufferOffset = (uint)bufferOffset,
                BufferImageHeight = (uint)Size.Height,
                BufferRowLength = (uint)(pixelStride * Size.Width),
                ImageSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                {
                    AspectMask = AspectMask,
                    MipLevel = 0,
                    BaseArrayLayer = (uint)arrayLayer,
                    LayerCount = (uint)layerCount
                },
                ImageOffset = new Offset3D
                {
                    X = 0,
                    Y = 0,
                    Z = 0
                },
                ImageExtent = new Extent3D
                {
                    Width = (uint)Size.Width,
                    Height = (uint)Size.Height,
                    Depth = 1
                }
            };

            TransitionLayout(commandBuffer, currentLayout, transferDst, 0, 1, arrayLayer, layerCount);
            api.CmdCopyImageToBuffer(commandBuffer.Buffer, mImage, transferDst.Layout, destination.Buffer, 1, copyRegion);
            TransitionLayout(commandBuffer, transferDst, currentLayout, 0, 1, arrayLayer, layerCount);
        }

        void IDeviceImage.TransitionLayout(ICommandList commandList, IDeviceImageLayout srcLayout, IDeviceImageLayout dstLayout)
        {
            if (commandList is not VulkanCommandBuffer || srcLayout is not VulkanImageLayout || dstLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            TransitionLayout((VulkanCommandBuffer)commandList, (VulkanImageLayout)srcLayout, (VulkanImageLayout)dstLayout, 0, MipLevels, 0, ArrayLayers);
        }

        public unsafe void TransitionLayout(VulkanCommandBuffer commandBuffer, VulkanImageLayout sourceLayout, VulkanImageLayout destinationLayout, int baseMipLevel, int levelCount, int arrayLayer, int layerCount)
        {
            using var transitionEvent = Profiler.GPUEvent(commandBuffer, "Transition layout");

            if (sourceLayout.Layout == destinationLayout.Layout)
            {
                return;
            }

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
                    BaseArrayLayer = (uint)arrayLayer,
                    LayerCount = (uint)layerCount
                }
            };

            var api = VulkanContext.API;
            api.CmdPipelineBarrier(commandBuffer.Buffer, sourceLayout.Stage, destinationLayout.Stage,
                                   DependencyFlags.None, 0, null, 0, null, 1, &barrier);
        }

        void IDeviceImage.CopyCubeFace(ICommandList commandList, int face, IDeviceImage source, IDeviceImageLayout currentLayout, IDeviceImageLayout sourceLayout)
        {
            if (commandList is not VulkanCommandBuffer || source is not VulkanImage || currentLayout is not VulkanImageLayout || sourceLayout is not VulkanImageLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyCubeFace((VulkanCommandBuffer)commandList, face, (VulkanImage)source, (VulkanImageLayout)currentLayout, (VulkanImageLayout)sourceLayout);
        }

        public unsafe void CopyCubeFace(VulkanCommandBuffer commandBuffer, int face, VulkanImage source, VulkanImageLayout currentLayout, VulkanImageLayout sourceLayout)
        {
            using var blitEvent = Profiler.GPUEvent(commandBuffer, "Copy cube face");

            if (Type != DeviceImageType.TypeCube || source.Type != DeviceImageType.Type2D)
            {
                throw new ArgumentException("Incompatible image types!");
            }

            if (Size != source.Size)
            {
                throw new ArgumentException("Mismatching size!");
            }

            if (face < 0 || face >= ArrayLayers)
            {
                throw new ArgumentOutOfRangeException(nameof(face));
            }

            var api = VulkanContext.API;
            var transferSrc = GetLayout(DeviceImageLayoutName.CopySource);
            var transferDst = GetLayout(DeviceImageLayoutName.CopyDestination);

            var copy = VulkanUtilities.Init<ImageCopy>() with
            {
                SrcSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                {
                    AspectMask = source.AspectMask,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                SrcOffset = VulkanUtilities.Init<Offset3D>(),
                DstSubresource = VulkanUtilities.Init<ImageSubresourceLayers>() with
                {
                    AspectMask = AspectMask,
                    MipLevel = 0,
                    BaseArrayLayer = (uint)face,
                    LayerCount = 1
                },
                DstOffset = VulkanUtilities.Init<Offset3D>(),
                Extent = VulkanUtilities.Init<Extent3D>() with
                {
                    Width = (uint)Size.Width,
                    Height = (uint)Size.Height,
                    Depth = 1
                }
            };

            TransitionLayout(commandBuffer, currentLayout, transferDst, 0, MipLevels, face, 1);
            source.TransitionLayout(commandBuffer, sourceLayout, transferSrc, 0, 1, 0, 1);

            api.CmdCopyImage(commandBuffer.Buffer, source.mImage, transferSrc.Layout, mImage, transferDst.Layout, 1, copy);
            source.TransitionLayout(commandBuffer, transferSrc, sourceLayout, 0, 1, 0, 1);

            GenerateMipmaps(commandBuffer, currentLayout, face, 1);
            TransitionLayout(commandBuffer, transferDst, currentLayout, MipLevels - 1, 1, face, 1);
        }

        void IDeviceImage.BlitImage(ICommandList commandList, IDeviceImage destination, IDeviceImageLayout sourceLayout, IDeviceImageLayout destinationLayout, SamplerFilter filter)
        {
            if (commandList is not VulkanCommandBuffer commandBuffer || destination is not VulkanImage vulkanImage ||
                sourceLayout is not VulkanImageLayout sourceVulkanLayout || destinationLayout is not VulkanImageLayout destinationVulkanLayout)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            BlitImage(commandBuffer, vulkanImage, 0, 1, sourceVulkanLayout, destinationVulkanLayout, filter);
        }

        public void BlitImage(VulkanCommandBuffer commandBuffer, VulkanImage destination, int baseLayer, int layerCount, VulkanImageLayout sourceLayout, VulkanImageLayout destinationLayout, SamplerFilter filter)
        {
            using var blitEvent = Profiler.GPUEvent(commandBuffer, "Blit image");

            var blit = VulkanUtilities.Init<ImageBlit>() with
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = AspectMask,
                    MipLevel = 0,
                    BaseArrayLayer = (uint)baseLayer,
                    LayerCount = (uint)layerCount
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = destination.AspectMask,
                    MipLevel = 0,
                    BaseArrayLayer = (uint)baseLayer,
                    LayerCount = (uint)layerCount
                }
            };

            blit.SrcOffsets[1] = new Offset3D
            {
                X = Size.Width,
                Y = Size.Height,
                Z = 1
            };

            blit.DstOffsets[1] = new Offset3D
            {
                X = destination.Size.Width,
                Y = destination.Size.Height,
                Z = 1
            };

            var api = VulkanContext.API;
            var blitFilter = VulkanTexture.ParseSamplerFilter(filter);
            api.CmdBlitImage(commandBuffer.Buffer, mImage, sourceLayout.Layout, destination.Image, destinationLayout.Layout, 1, blit, blitFilter);

            destination.GenerateMipmaps(commandBuffer, destinationLayout, baseLayer, layerCount);
        }

        ITexture IDeviceImage.CreateTexture(bool ownsImage, ISamplerSettings? samplerSettings)
        {
            return new VulkanTexture(this, ownsImage, samplerSettings);
        }

        protected override unsafe void BindSets(DescriptorSet[] sets, uint binding, uint arrayElement)
        {
            using var bindEvent = Profiler.Event();
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
        public DeviceImageType Type { get; }
        public Size Size { get; }
        public int MipLevels { get; }
        public int ArrayLayers { get; }
        public DeviceImageFormat ImageFormat { get; }
        public Format VulkanFormat { get; }
        public ImageAspectFlags AspectMask { get; }
        public ImageTiling Tiling { get; }
        public ImageView View => mView;
        public Silk.NET.Vulkan.Image Image => mImage;

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

        IDeviceImageLayout IDeviceImage.Layout
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