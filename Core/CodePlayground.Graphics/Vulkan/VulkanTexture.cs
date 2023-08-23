using Silk.NET.Vulkan;
using System;

using OptickMacros = Optick.NET.OptickMacros;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanTexture : BindableVulkanImage, ITexture
    {
        public VulkanTexture(VulkanImage image, bool ownsImage, ISamplerSettings? samplerSettings)
        {
            using var constructorEvent = OptickMacros.Event();

            mDisposed = false;
            mDevice = image.Device;

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
            using var disposeEvent = OptickMacros.Event();
            DestroyDynamicIDs();

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
            using var changedEvent = OptickMacros.Event();

            mImageInfo.ImageLayout = layout.Layout;
            Rebind();
        }

        public unsafe void InvalidateSampler()
        {
            using var invalidateEvent = OptickMacros.Event();
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

        protected override unsafe void BindSets(DescriptorSet[] sets, uint binding, uint arrayElement)
        {
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
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = imageInfo
                    };
                }

                var api = VulkanContext.API;
                api.UpdateDescriptorSets(mDevice.Device, writes, 0, null);
            }
        }

        public VulkanImage Image { get; }
        public bool OwnsImage { get; }
        public ISamplerSettings? SamplerSettings { get; }

        IDeviceImage ITexture.Image => Image;

        private readonly VulkanDevice mDevice;
        private DescriptorImageInfo mImageInfo;

        private bool mDisposed;
    }
}