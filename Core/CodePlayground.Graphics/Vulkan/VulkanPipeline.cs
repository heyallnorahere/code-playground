using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanPipelineDescriptorSet
    {
        public DescriptorSetLayout Layout { get; set; }
        public DescriptorSet[] Sets { get; set; }
    }

    public interface IBindableVulkanResource
    {
        public void Bind(DescriptorSet[] sets, int binding, int index);
    }

    public sealed class VulkanPipeline : IPipeline
    {
        public unsafe VulkanPipeline(VulkanDevice device, PipelineDescription description)
        {
            mDesc = description;
            mReflectionData = new Dictionary<ShaderStage, ShaderReflectionResult>();

            mDescriptorSets = new Dictionary<int, VulkanPipelineDescriptorSet>();
            mDevice = device;

            mLoaded = false;
            mDisposed = false;

            const uint maxSets = 50; // placeholder value
            const uint descriptorTypeCount = 10; // see VkDescriptorType

            var poolSizes = new DescriptorPoolSize[descriptorTypeCount];
            for (uint i = 0; i < descriptorTypeCount; i++)
            {
                poolSizes[i] = new DescriptorPoolSize
                {
                    Type = (DescriptorType)i,
                    DescriptorCount = maxSets * 16 // just to be safe
                };
            }

            fixed (DescriptorPoolSize* poolSizePtr = poolSizes)
            {
                var createInfo = VulkanUtilities.Init<DescriptorPoolCreateInfo>() with
                {
                    Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                    MaxSets = maxSets,
                    PoolSizeCount = descriptorTypeCount,
                    PPoolSizes = poolSizePtr
                };

                fixed (DescriptorPool* pool = &mDescriptorPool)
                {
                    var api = VulkanContext.API;
                    api.CreateDescriptorPool(device.Device, &createInfo, null, pool).Assert();
                }
            }
        }

        ~VulkanPipeline() => Dispose();
        public unsafe void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Cleanup();

            var api = VulkanContext.API;
            api.DestroyDescriptorPool(mDevice.Device, mDescriptorPool, null);

            mDisposed = true;
        }

        private unsafe void Cleanup()
        {
            if (!mLoaded)
            {
                return;
            }

            var api = VulkanContext.API;
            api.DestroyPipeline(mDevice.Device, mPipeline, null);
            api.DestroyPipelineLayout(mDevice.Device, mLayout, null);

            foreach (int set in mDescriptorSets.Keys)
            {
                var setData = mDescriptorSets[set];

                api.FreeDescriptorSets(mDevice.Device, mDescriptorPool, setData.Sets);
                api.DestroyDescriptorSetLayout(mDevice.Device, setData.Layout, null);
            }

            mDescriptorSets.Clear();
            mReflectionData.Clear();

            mLoaded = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssertLoaded()
        {
            if (!mLoaded)
            {
                throw new InvalidOperationException("This pipeline has not loaded a shader!");
            }
        }

        void IPipeline.Bind(ICommandList commandList, int frame)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer to bind to!");
            }

            Bind((VulkanCommandBuffer)commandList, frame);
        }

        public void Bind(VulkanCommandBuffer commandBuffer, int frame)
        {
            AssertLoaded();

            var api = VulkanContext.API;
            var buffer = commandBuffer.Buffer;
            var bindPoint = mDesc.Type switch
            {
                PipelineType.Graphics => PipelineBindPoint.Graphics,
                PipelineType.Compute => PipelineBindPoint.Compute,
                _ => throw new InvalidOperationException($"Invalid pipeline type: {mDesc.Type}")
            };

            api.CmdBindPipeline(buffer, bindPoint, mPipeline);
            foreach (int set in mDescriptorSets.Keys)
            {
                var setData = mDescriptorSets[set];
                api.CmdBindDescriptorSets(buffer, bindPoint, mLayout, (uint)set, 1, setData.Sets[frame], 1, 0);
            }
        }

        bool IPipeline.Bind(IDeviceBuffer buffer, string name, int index)
        {
            if (buffer is IBindableVulkanResource resource)
            {
                return Bind(resource, name, index);
            }
            else
            {
                throw new ArgumentException("Must pass a bindable Vulkan resource!");
            }
        }

        public bool Bind(IBindableVulkanResource resource, string name, int index)
        {
            AssertLoaded();
            if (!FindResource(name, out int set, out int binding))
            {
                return false;
            }

            Bind(resource, set, binding, index);
            return true;
        }

        public void Bind(IBindableVulkanResource resource, int set, int binding, int index)
        {
            AssertLoaded();

            var sets = mDescriptorSets[set].Sets;
            resource.Bind(sets, binding, index);
        }

        public bool FindResource(string name, out int set, out int binding)
        {
            foreach (var data in mReflectionData.Values)
            {
                foreach (int currentSet in data.Resources.Keys)
                {
                    var setResources = data.Resources[currentSet];
                    foreach (int currentBinding in setResources.Keys)
                    {
                        var currentResource = setResources[currentBinding];
                        if (currentResource.Name == name)
                        {
                            set = currentSet;
                            binding = currentBinding;

                            return true;
                        }
                    }
                }
            }

            set = binding = -1;
            return false;
        }

        public void Load(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            Cleanup();

            // todo: load shader stages

            mLoaded = true;
        }

        public PipelineDescription Description => mDesc;

        private readonly PipelineDescription mDesc;
        private readonly Dictionary<ShaderStage, ShaderReflectionResult> mReflectionData;

        private Pipeline mPipeline;
        private PipelineLayout mLayout;

        private readonly Dictionary<int, VulkanPipelineDescriptorSet> mDescriptorSets;
        private readonly DescriptorPool mDescriptorPool;

        private readonly VulkanDevice mDevice;
        private bool mLoaded, mDisposed;
    }
}
