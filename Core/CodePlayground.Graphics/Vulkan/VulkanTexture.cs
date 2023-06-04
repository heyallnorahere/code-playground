using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct BoundPipelineDescriptorSets
    {
        public Dictionary<int, BoundPipelineBindings> Bindings { get; set; }
    }

    internal struct BoundPipelineBindings
    {
        public HashSet<int> BoundIndices { get; set; }
    }

    public sealed class VulkanTexture : ITexture, IBindableVulkanResource
    {
        public VulkanTexture(VulkanImage image, bool ownsImage, ISamplerSettings? samplerSettings)
        {
            mDisposed = false;
            mDevice = image.Device;

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

        private void OnLayoutChanged(VulkanImageLayout layout)
        {
            mImageInfo.ImageLayout = layout.Layout;
            // todo: rebind
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
                MaxLod = 0f,
                BorderColor = BorderColor.IntOpaqueBlack,
                UnnormalizedCoordinates = false
            };

            fixed (Sampler* sampler = &mImageInfo.Sampler)
            {
                var api = VulkanContext.API;
                api.CreateSampler(mDevice.Device, &createInfo, null, sampler).Assert();
            }

            // todo: rebind
        }

        public void Bind(DescriptorSet[] sets, int set, int binding, int index, VulkanPipeline pipeline)
        {
            // todo: bind
        }

        public void Unbind(int set, int binding, int index, VulkanPipeline pipeline)
        {
            // todo: unbind
        }

        public VulkanImage Image { get; }
        public bool OwnsImage { get; }
        public ISamplerSettings? SamplerSettings { get; }
        public ulong ID { get; }

        IDeviceImage ITexture.Image => Image;

        private bool mDisposed;
        private readonly VulkanDevice mDevice;
        private DescriptorImageInfo mImageInfo;
    }
}