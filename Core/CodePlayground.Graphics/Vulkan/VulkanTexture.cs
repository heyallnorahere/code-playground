using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

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

    public sealed class VulkanTexture : ITexture, IBindableVulkanResource
    {
        public VulkanTexture(VulkanImage image, bool ownsImage, ISamplerSettings? samplerSettings)
        {
            mDisposed = false;
            mDevice = image.Device;
            mBoundPipelines = new Dictionary<ulong, BoundPipelineData>();

            ID = VulkanPipeline.GenerateID();
            Image = image;
            OwnsImage = ownsImage;
            SamplerSettings = samplerSettings;

            mImageInfo = VulkanUtilities.Init<DescriptorImageInfo>() with
            {
                ImageView = image.View,
                ImageLayout = image.Layout.Layout
            };

            InvalidateSampler();
            Image.OnLayoutChanged += OnLayoutChanged;
        }

        ~VulkanTexture()
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
                if (OwnsImage)
                {
                    Image.Dispose();
                }
                else
                {
                    Image.OnLayoutChanged -= OnLayoutChanged;
                }
            }

            var api = VulkanContext.API;
            api.DestroySampler(mDevice.Device, mImageInfo.Sampler, null);
        }

        private void Rebind()
        {
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

        private void OnLayoutChanged(VulkanImageLayout layout)
        {
            mImageInfo.ImageLayout = layout.Layout;
            Rebind();
        }

        public unsafe void InvalidateSampler()
        {
            if (mImageInfo.Sampler.Handle != 0)
            {
                var api = VulkanContext.API;
                api.DestroySampler(mDevice.Device, mImageInfo.Sampler, null);
            }

            var physicalDevice = mDevice.PhysicalDevice;
            physicalDevice.GetFeatures(out PhysicalDeviceFeatures features);
            physicalDevice.GetProperties(out PhysicalDeviceProperties properties);

            var addressMode = (SamplerSettings?.AddressMode ?? default) switch
            {
                AddressMode.Repeat => SamplerAddressMode.Repeat,
                AddressMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
                AddressMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
                AddressMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
                AddressMode.MirrorClampToEdge => SamplerAddressMode.MirrorClampToEdge,
                _ => throw new ArgumentException("Invalid address mode!")
            };

            var filter = (SamplerSettings?.Filter ?? default) switch
            {
                SamplerFilter.Linear => Filter.Linear,
                SamplerFilter.Nearest => Filter.Nearest,
                SamplerFilter.Cubic => Filter.CubicExt,
                _ => throw new ArgumentException("Invalid sampler filter!")
            };

            var createInfo = VulkanUtilities.Init<SamplerCreateInfo>() with
            {
                MagFilter = filter,
                MinFilter = filter,
                AddressModeU = addressMode,
                AddressModeV = addressMode,
                AddressModeW = addressMode,
                MipLodBias = 0f,
                AnisotropyEnable = features.SamplerAnisotropy,
                MaxAnisotropy = properties.Limits.MaxSamplerAnisotropy,
                CompareEnable = false,
                CompareOp = CompareOp.Always,
                MinLod = 0f,
                MaxLod = Image.MipLevels,
                BorderColor = BorderColor.IntOpaqueBlack,
                UnnormalizedCoordinates = false
            };

            fixed (Sampler* sampler = &mImageInfo.Sampler)
            {
                var api = VulkanContext.API;
                api.CreateSampler(mDevice.Device, &createInfo, null, sampler).Assert();
            }

            Rebind();
        }

        private void AddBindingIndex(int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
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

        public unsafe void Bind(DescriptorSet[] sets, int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
            AddBindingIndex(set, binding, index, pipeline, dynamicId);

            fixed (DescriptorImageInfo* imageInfo = &mImageInfo)
            {
                var writes = new WriteDescriptorSet[sets.Length];
                for (int i = 0; i < writes.Length; i++)
                {
                    writes[i] = VulkanUtilities.Init<WriteDescriptorSet>() with
                    {
                        DstSet = sets[i],
                        DstBinding = (uint)binding,
                        DstArrayElement = (uint)index,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = imageInfo
                    };
                }

                var api = VulkanContext.API;
                api.UpdateDescriptorSets(mDevice.Device, writes, 0, null);
            }
        }

        public void Unbind(int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
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

        public VulkanImage Image { get; }
        public bool OwnsImage { get; }
        public ISamplerSettings? SamplerSettings { get; }
        public ulong ID { get; }

        IDeviceImage ITexture.Image => Image;

        private bool mDisposed;
        private readonly VulkanDevice mDevice;
        private DescriptorImageInfo mImageInfo;
        private readonly Dictionary<ulong, BoundPipelineData> mBoundPipelines;
    }
}