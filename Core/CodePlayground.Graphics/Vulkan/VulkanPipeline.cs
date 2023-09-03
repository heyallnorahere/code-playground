using Optick.NET;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanPipelineDescriptorSet
    {
        public DescriptorSetLayout Layout;
        public DescriptorSet[] Sets;
    }

    internal struct DynamicID
    {
        public IBindableVulkanResource Resource { get; set; }
        public int Set { get; set; }
        public int Binding { get; set; }
        public int Index { get; set; }
        public DescriptorSet DescriptorSet { get; set; }
        public string Name { get; set; }
    }

    internal struct ReflectionCache
    {
        public ShaderStage Stage { get; set; }
        public int Set { get; set; }
        public int Binding { get; set; }
    }

    internal struct DescriptorSetPipelineResources
    {
        public Dictionary<int, BindingPipelineResources> Bindings { get; set; }
    }

    internal struct BindingPipelineResources
    {
        public Dictionary<int, IBindableVulkanResource> BoundResources { get; set; }
    }

    public interface IBindableVulkanResource
    {
        public ulong ID { get; }

        public void Bind(DescriptorSet[] sets, int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId);
        public void Unbind(int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId);
    }

    public sealed class VulkanPipeline : IPipeline
    {
        private static readonly IReadOnlyDictionary<ShaderTypeClass, IReadOnlyDictionary<int, Format>> sAttributeFormats;
        private static ulong sCurrentID;

        static VulkanPipeline()
        {
            sCurrentID = 0;
            sAttributeFormats = new Dictionary<ShaderTypeClass, IReadOnlyDictionary<int, Format>>
            {
                [ShaderTypeClass.Float] = new Dictionary<int, Format>
                {
                    [1] = Format.R32Sfloat,
                    [2] = Format.R32G32Sfloat,
                    [3] = Format.R32G32B32Sfloat,
                    [4] = Format.R32G32B32A32Sfloat
                },
                [ShaderTypeClass.SInt] = new Dictionary<int, Format>
                {
                    [1] = Format.R32Sint,
                    [2] = Format.R32G32Sint,
                    [3] = Format.R32G32B32Sint,
                    [4] = Format.R32G32B32A32Sint
                },
                [ShaderTypeClass.UInt] = new Dictionary<int, Format>
                {
                    [1] = Format.R32Uint,
                    [2] = Format.R32G32Uint,
                    [3] = Format.R32G32B32Uint,
                    [4] = Format.R32G32B32A32Uint
                }
            };
        }

        public static ShaderStageFlags ConvertStage(ShaderStage stage)
        {
            return stage switch
            {
                ShaderStage.Vertex => ShaderStageFlags.VertexBit,
                ShaderStage.Fragment => ShaderStageFlags.FragmentBit,
                ShaderStage.Geometry => ShaderStageFlags.GeometryBit,
                ShaderStage.Compute => ShaderStageFlags.ComputeBit,
                _ => throw new ArgumentException("Invalid shader stage!")
            };
        }

        public static ulong GenerateID() => sCurrentID++;

        public unsafe VulkanPipeline(VulkanContext context, PipelineDescription description)
        {
            using var constructorEvent = OptickMacros.Event();

            mDesc = description;
            mReflectionView = null;
            mPushConstantBufferOffsets = new Dictionary<string, int>();
            mBoundResources = new Dictionary<int, DescriptorSetPipelineResources>();
            mID = GenerateID();
            mCurrentDynamicID = 0;

            mReflectionCache = new Dictionary<string, ReflectionCache>();
            mDynamicIDs = new Dictionary<nint, DynamicID>();
            mDescriptorSets = new Dictionary<int, VulkanPipelineDescriptorSet>();
            mDevice = context.Device;
            mCompiler = context.CreateCompiler();

            mLoaded = false;
            mDisposed = false;

            using (OptickMacros.Event("Descriptor pool creation"))
            {
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
                        api.CreateDescriptorPool(mDevice.Device, &createInfo, null, pool).Assert();
                    }
                }
            }
        }

        ~VulkanPipeline()
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

            if (disposing)
            {
                foreach (int set in mBoundResources.Keys)
                {
                    var setData = mBoundResources[set];
                    foreach (int binding in setData.Bindings.Keys)
                    {
                        var bindingData = setData.Bindings[binding];
                        foreach (int index in bindingData.BoundResources.Keys)
                        {
                            var resource = bindingData.BoundResources[index];
                            resource.Unbind(set, binding, index, this, -1);
                        }
                    }
                }

                foreach (var id in mDynamicIDs.Keys)
                {
                    var data = mDynamicIDs[id];
                    data.Resource.Unbind(data.Set, data.Binding, data.Index, this, id);
                }
            }

            Cleanup();

            var api = VulkanContext.API;
            api.DestroyDescriptorPool(mDevice.Device, mDescriptorPool, null);
        }

        private unsafe void Cleanup()
        {
            using var cleanupEvent = OptickMacros.Event();
            if (!mLoaded)
            {
                return;
            }

            var api = VulkanContext.API;
            api.DestroyPipeline(mDevice.Device, mPipeline, null);
            api.DestroyPipelineLayout(mDevice.Device, mLayout, null);

            foreach (var dynamicIdData in mDynamicIDs.Values)
            {
                api.FreeDescriptorSets(mDevice.Device, mDescriptorPool, 1, dynamicIdData.DescriptorSet).Assert();
            }

            foreach (int set in mDescriptorSets.Keys)
            {
                var setData = mDescriptorSets[set];

                api.FreeDescriptorSets(mDevice.Device, mDescriptorPool, setData.Sets).Assert();
                api.DestroyDescriptorSetLayout(mDevice.Device, setData.Layout, null);
            }

            mReflectionCache.Clear();
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
            using var bindEvent = OptickMacros.Event();
            using var gpuBindEvent = OptickMacros.GPUEvent("Bind pipeline");

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
            using (OptickMacros.GPUEvent("Bind pipeline descriptor sets"))
            {
                foreach (int set in mDescriptorSets.Keys)
                {
                    if (!mBoundResources.ContainsKey(set))
                    {
                        continue;
                    }

                    var setData = mDescriptorSets[set];
                    api.CmdBindDescriptorSets(buffer, bindPoint, mLayout, (uint)set, 1, setData.Sets[frame], 0, 0);
                }
            }
        }

        void IPipeline.Bind(ICommandList commandList, nint id)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer to bind to!");
            }

            Bind((VulkanCommandBuffer)commandList, id);
        }

        public void Bind(VulkanCommandBuffer commandBuffer, nint id)
        {
            using var bindEvent = OptickMacros.Event();
            using var gpuBindEvent = OptickMacros.Event("Bind pipeline dynamic ID descriptor set");

            if (!mDynamicIDs.TryGetValue(id, out DynamicID data))
            {
                throw new ArgumentException($"Invalid ID: 0x{id:X}");
            }

            var bindPoint = mDesc.Type switch
            {
                PipelineType.Graphics => PipelineBindPoint.Graphics,
                PipelineType.Compute => PipelineBindPoint.Compute,
                _ => throw new InvalidOperationException($"Invalid pipeline type: {mDesc.Type}")
            };

            var api = VulkanContext.API;
            api.CmdBindDescriptorSets(commandBuffer.Buffer, bindPoint, mLayout, (uint)data.Set, 1, data.DescriptorSet, 0, 0);
        }

        void IPipeline.PushConstants(ICommandList commandList, BufferMapCallback callback)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer to bind to!");
            }

            PushConstants((VulkanCommandBuffer)commandList, callback);
        }

        public unsafe void PushConstants(VulkanCommandBuffer commandBuffer, BufferMapCallback callback)
        {
            using var pushConstantsEvent = OptickMacros.Event();
            using var gpuPushConstantsEvent = OptickMacros.GPUEvent("Push constants");

            mReflectionView!.ProcessPushConstantBuffers(out int size, out ShaderStageFlags stages);

            var buffer = new byte[size];
            var span = new Span<byte>(buffer);
            callback.Invoke(span);

            fixed (byte* data = buffer)
            {
                var api = VulkanContext.API;
                api.CmdPushConstants(commandBuffer.Buffer, mLayout, stages, 0, (uint)size, data);
            }
        }

        bool IPipeline.Bind(IDeviceBuffer buffer, string name, int index) => BindObject(buffer, name, index, Bind);
        bool IPipeline.Bind(IDeviceImage image, string name, int index) => BindObject(image, name, index, Bind);
        bool IPipeline.Bind(ITexture texture, string name, int index) => BindObject(texture, name, index, Bind);

        private static T BindObject<T>(object passedObject, string name, int index, Func<IBindableVulkanResource, string, int, T> callback)
        {
            using var bindEvent = OptickMacros.Event();
            if (passedObject is IBindableVulkanResource resource)
            {
                return callback.Invoke(resource, name, index);
            }
            else
            {
                throw new ArgumentException("Must pass a bindable Vulkan resource!");
            }
        }

        private bool FindResource(string name, out ShaderStage stage, out int set, out int binding)
        {
            if (mReflectionCache.TryGetValue(name, out ReflectionCache cache))
            {
                stage = cache.Stage;
                set = cache.Set;
                binding = cache.Binding;

                return true;
            }

            AssertLoaded();
            if (mReflectionView!.FindResource(name, out stage, out set, out binding))
            {
                mReflectionCache.Add(name, new ReflectionCache
                {
                    Stage = stage,
                    Set = set,
                    Binding = binding
                });

                return true;
            }

            return false;
        }

        public bool Bind(IBindableVulkanResource resource, string name, int index)
        {
            using var bindEvent = OptickMacros.Event();
            if (!FindResource(name, out _, out int set, out int binding))
            {
                return false;
            }

            Bind(resource, set, binding, index);
            return true;
        }

        private bool BindResource(int set, int binding, int index, IBindableVulkanResource resource)
        {
            using var bindResourceEvent = OptickMacros.Event();
            if (!mBoundResources.TryGetValue(set, out DescriptorSetPipelineResources setData))
            {
                mBoundResources.Add(set, setData = new DescriptorSetPipelineResources
                {
                    Bindings = new Dictionary<int, BindingPipelineResources>()
                });
            }

            if (!setData.Bindings.TryGetValue(binding, out BindingPipelineResources bindingData))
            {
                setData.Bindings.Add(binding, bindingData = new BindingPipelineResources
                {
                    BoundResources = new Dictionary<int, IBindableVulkanResource>()
                });
            }

            if (bindingData.BoundResources.TryGetValue(index, out IBindableVulkanResource? existing) && existing is not null)
            {
                if (existing.ID == resource.ID)
                {
                    return false;
                }

                existing.Unbind(set, binding, index, this, -1);
            }

            bindingData.BoundResources[index] = resource;
            return true;
        }

        public void Bind(IBindableVulkanResource resource, int set, int binding, int index)
        {
            using var bindEvent = OptickMacros.Event();

            AssertLoaded();
            if (!BindResource(set, binding, index, resource))
            {
                return;
            }

            var sets = mDescriptorSets[set].Sets;
            resource.Bind(sets, set, binding, index, this, -1);
        }

        nint IPipeline.CreateDynamicID(IDeviceBuffer buffer, string name, int index) => BindObject(buffer, name, index, CreateDynamicID);
        nint IPipeline.CreateDynamicID(ITexture texture, string name, int index) => BindObject(texture, name, index, CreateDynamicID);

        private unsafe DescriptorSet AllocateDescriptorSet(int set)
        {
            using var allocateEvent = OptickMacros.Event();
            if (!mDescriptorSets.TryGetValue(set, out VulkanPipelineDescriptorSet setData))
            {
                return default;
            }

            var allocInfo = VulkanUtilities.Init<DescriptorSetAllocateInfo>() with
            {
                DescriptorPool = mDescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setData.Layout
            };

            var api = VulkanContext.API;
            api.AllocateDescriptorSets(mDevice.Device, allocInfo, out DescriptorSet result).Assert();

            return result;
        }

        public nint CreateDynamicID(IBindableVulkanResource resource, string name, int index)
        {
            using var createEvent = OptickMacros.Event();
            foreach (nint existingId in mDynamicIDs.Keys)
            {
                var existingData = mDynamicIDs[existingId];
                if (existingData.Name == name && existingData.Resource.ID == resource.ID)
                {
                    return existingId;
                }
            }

            if (!FindResource(name, out _, out int set, out int binding))
            {
                throw new ArgumentException($"Failed to find resource: {resource}");
            }

            if (mReflectionView!.GetDescriptorSetBindingCount(set) > 1)
            {
                throw new InvalidOperationException("Cannot create a dynamic ID on a set with more than 1 binding!");
            }

            var descriptorSet = AllocateDescriptorSet(set);
            var id = mCurrentDynamicID++;
            resource.Bind(new DescriptorSet[] { descriptorSet }, set, binding, index, this, id);

            mDynamicIDs.Add(id, new DynamicID
            {
                Resource = resource,
                Set = set,
                Binding = binding,
                Index = index,
                DescriptorSet = descriptorSet,
                Name = name
            });

            return id;
        }

        public void UpdateDynamicID(nint id)
        {
            using var updateEvent = OptickMacros.Event();
            if (!mDynamicIDs.ContainsKey(id))
            {
                throw new ArgumentException($"No such ID: 0x{id:X}");
            }

            var data = mDynamicIDs[id];
            data.Resource.Bind(new DescriptorSet[] { data.DescriptorSet }, data.Set, data.Binding, data.Index, this, id);
        }

        public void DestroyDynamicID(nint id)
        {
            using var destroyEvent = OptickMacros.Event();
            if (!mDynamicIDs.ContainsKey(id))
            {
                return;
            }

            var data = mDynamicIDs[id];
            data.Resource.Unbind(data.Set, data.Binding, data.Index, this, id);

            if (data.DescriptorSet.Handle != 0)
            {
                var api = VulkanContext.API;
                api.FreeDescriptorSets(mDevice.Device, mDescriptorPool, 1, data.DescriptorSet).Assert();
            }

            mDynamicIDs.Remove(id);
        }

        public void Load(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            using var loadEvent = OptickMacros.Event();
            Cleanup();

            var filteredStages = shaders.Keys.Where(stage => mDesc.Type switch
            {
                PipelineType.Graphics => stage != ShaderStage.Compute,
                PipelineType.Compute => stage == ShaderStage.Compute,
                _ => throw new ArgumentException($"Invalid pipeline type: {mDesc.Type}")
            });

            if (!filteredStages.Any())
            {
                throw new ArgumentException($"No applicable shaders provided for a {mDesc.Type.ToString().ToLower()} pipeline!");
            }

            var filteredShaders = new Dictionary<ShaderStage, IShader>();
            foreach (var stage in filteredStages)
            {
                filteredShaders.Add(stage, shaders[stage]);
            }

            mReflectionView = new VulkanReflectionView(filteredShaders);
            CreateDescriptorSets();
            CreatePipelineLayout();

            switch (mDesc.Type)
            {
                case PipelineType.Graphics:
                    CreateGraphicsPipeline(filteredShaders);
                    break;
                case PipelineType.Compute:
                    CreateComputePipeline(filteredShaders[ShaderStage.Compute]);
                    break;
            }

            mLoaded = true;
        }

        private unsafe void CreateDescriptorSets()
        {
            using var createEvent = OptickMacros.Event();

            var layoutBindings = new Dictionary<int, List<DescriptorSetLayoutBinding>>();
            var reflectionData = mReflectionView!.ReflectionData;

            using (OptickMacros.Event("Parse reflection data"))
            {
                foreach (var stage in reflectionData.Keys)
                {
                    var stageReflectionData = reflectionData[stage];
                    foreach (int set in stageReflectionData.Resources.Keys)
                    {
                        if (!layoutBindings.ContainsKey(set))
                        {
                            layoutBindings.Add(set, new List<DescriptorSetLayoutBinding>());
                        }

                        var bindingList = layoutBindings[set];
                        var setBindings = stageReflectionData.Resources[set];

                        foreach (int binding in setBindings.Keys)
                        {
                            var resource = setBindings[binding];
                            var typeData = stageReflectionData.Types[resource.Type];

                            uint descriptorCount = 1;
                            if (typeData.ArrayDimensions is not null)
                            {
                                foreach (var dimension in typeData.ArrayDimensions)
                                {
                                    descriptorCount *= (uint)dimension;
                                }
                            }

                            var bindingData = VulkanUtilities.Init<DescriptorSetLayoutBinding>() with
                            {
                                Binding = (uint)binding,
                                DescriptorType = resource.ResourceType switch
                                {
                                    ShaderResourceTypeFlags.Image | ShaderResourceTypeFlags.Sampler => DescriptorType.CombinedImageSampler,
                                    ShaderResourceTypeFlags.Image => DescriptorType.SampledImage,
                                    ShaderResourceTypeFlags.Sampler => DescriptorType.Sampler,
                                    ShaderResourceTypeFlags.UniformBuffer => DescriptorType.UniformBuffer,
                                    ShaderResourceTypeFlags.StorageBuffer => DescriptorType.StorageBuffer,
                                    ShaderResourceTypeFlags.StorageImage => DescriptorType.StorageImage,
                                    _ => throw new InvalidOperationException($"Flag combination not implemented: {resource.ResourceType}")
                                },
                                DescriptorCount = descriptorCount,
                                StageFlags = ShaderStageFlags.All
                            };

                            int existingIndex = bindingList.FindIndex(desc => desc.Binding == binding);
                            if (existingIndex < 0)
                            {
                                bindingList.Add(bindingData);
                            }
                            else if (bindingList[existingIndex].DescriptorType != bindingData.DescriptorType)
                            {
                                throw new InvalidOperationException($"Duplicate resource in set {set} binding {binding}!");
                            }
                        }
                    }
                }
            }

            using (OptickMacros.Event("Allocate main sets"))
            {
                mDescriptorSets.Clear();
                foreach (int set in layoutBindings.Keys)
                {
                    var setData = new VulkanPipelineDescriptorSet
                    {
                        Sets = new DescriptorSet[mDesc.FrameCount]
                    };

                    var api = VulkanContext.API;
                    var bindings = layoutBindings[set].ToArray();
                    fixed (DescriptorSetLayoutBinding* bindingPtr = bindings)
                    {
                        var createInfo = VulkanUtilities.Init<DescriptorSetLayoutCreateInfo>() with
                        {
                            Flags = DescriptorSetLayoutCreateFlags.None,
                            BindingCount = (uint)bindings.Length,
                            PBindings = bindingPtr
                        };

                        api.CreateDescriptorSetLayout(mDevice.Device, &createInfo, null, &setData.Layout).Assert();
                    }

                    var layouts = new DescriptorSetLayout[mDesc.FrameCount];
                    Array.Fill(layouts, setData.Layout);

                    fixed (DescriptorSetLayout* layoutPtr = layouts)
                    {
                        var allocInfo = VulkanUtilities.Init<DescriptorSetAllocateInfo>() with
                        {
                            DescriptorPool = mDescriptorPool,
                            DescriptorSetCount = (uint)mDesc.FrameCount,
                            PSetLayouts = layoutPtr
                        };

                        fixed (DescriptorSet* setPtr = setData.Sets)
                        {
                            api.AllocateDescriptorSets(mDevice.Device, &allocInfo, setPtr).Assert();
                        }
                    }

                    mDescriptorSets.Add(set, setData);
                }
            }

            using (OptickMacros.Event("Reallocate dynamic ID sets"))
            {
                var destroyedIds = new HashSet<nint>();
                foreach (nint id in mDynamicIDs.Keys)
                {
                    var data = mDynamicIDs[id];

                    var set = AllocateDescriptorSet(data.Set);
                    if (set.Handle == 0)
                    {
                        // just destroy the ID and move on
                        destroyedIds.Add(id);
                        data.DescriptorSet = default;
                    }
                    else
                    {
                        data.DescriptorSet = set;
                    }

                    mDynamicIDs[id] = data;
                }

                foreach (nint id in destroyedIds)
                {
                    DestroyDynamicID(id);
                }
            }
        }

        private unsafe void CreatePipelineLayout()
        {
            using var createEvent = OptickMacros.Event();
            mPushConstantBufferOffsets.Clear();

            var setIndices = mDescriptorSets.Keys.ToList();
            setIndices.Sort((a, b) => -a.CompareTo(b));

            var setLayouts = new DescriptorSetLayout[setIndices.Count > 0 ? (setIndices[0] + 1) : 0];
            foreach (var set in mDescriptorSets.Keys)
            {
                setLayouts[set] = mDescriptorSets[set].Layout;
            }

            var pushConstantRanges = new Dictionary<string, PushConstantRange>();
            using (OptickMacros.Event("Process push constant ranges"))
            {
                int currentOffset = 0;
                var reflectionData = mReflectionView!.ReflectionData;

                foreach (var stage in reflectionData.Keys)
                {
                    var data = reflectionData[stage];
                    foreach (var pushConstantBuffer in data.PushConstantBuffers)
                    {
                        var name = pushConstantBuffer.Name;
                        var typeData = data.Types[pushConstantBuffer.Type];
                        var size = typeData.TotalSize;

                        if (!pushConstantRanges.ContainsKey(name))
                        {
                            pushConstantRanges.Add(name, new PushConstantRange
                            {
                                StageFlags = ConvertStage(stage),
                                Offset = (uint)currentOffset,
                                Size = (uint)size
                            });

                            mPushConstantBufferOffsets.Add(name, currentOffset);
                            currentOffset += size;
                        }
                        else if (pushConstantRanges[name].Size != (uint)size)
                        {
                            throw new InvalidOperationException("Mismatching push constant buffer sizes!");
                        }
                        else
                        {
                            var range = pushConstantRanges[name];
                            range.StageFlags |= ConvertStage(stage);
                            pushConstantRanges[name] = range;
                        }
                    }
                }
            }

            fixed (DescriptorSetLayout* layoutPtr = setLayouts)
            {
                var ranges = pushConstantRanges.Values.ToArray();
                fixed (PushConstantRange* rangePtr = ranges)
                {
                    var createInfo = VulkanUtilities.Init<PipelineLayoutCreateInfo>() with
                    {
                        Flags = PipelineLayoutCreateFlags.None,
                        SetLayoutCount = (uint)setLayouts.Length,
                        PSetLayouts = layoutPtr,
                        PushConstantRangeCount = (uint)ranges.Length,
                        PPushConstantRanges = rangePtr
                    };

                    fixed (PipelineLayout* layout = &mLayout)
                    {
                        var api = VulkanContext.API;
                        api.CreatePipelineLayout(mDevice.Device, &createInfo, null, layout).Assert();
                    }
                }
            }
        }

        private static Format GetAttributeFormat(ReflectedShaderType attributeType)
        {
            using var getFormatEvent = OptickMacros.Event();
            if (attributeType.Columns != 1 || (attributeType.ArrayDimensions?.Any() ?? false))
            {
                throw new InvalidOperationException("Every vertex attribute must be a single vector!");
            }

            if (attributeType.Size != 4)
            {
                throw new InvalidOperationException("Vector columns must be 32-bit integers or floats!");
            }

            if (!sAttributeFormats.ContainsKey(attributeType.Class))
            {
                throw new InvalidOperationException($"Unsupported vector type: {attributeType.Class}");
            }

            var formats = sAttributeFormats[attributeType.Class];
            if (!formats.ContainsKey(attributeType.Rows))
            {
                throw new InvalidOperationException($"Invalid vector size: {attributeType.Rows}");
            }

            return formats[attributeType.Rows];
        }

        private unsafe void CreateGraphicsPipeline(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            using var createEvent = OptickMacros.Event();
            if (!shaders.ContainsKey(ShaderStage.Vertex) || !shaders.ContainsKey(ShaderStage.Fragment))
            {
                throw new ArgumentException("A graphics pipeline must consist of both a vertex & fragment shader!");
            }

            if (mDesc.RenderTarget is not VulkanRenderPass)
            {
                throw new ArgumentException("Must pass a Vulkan render pass!");
            }

            var stages = shaders.Keys.ToList();
            var shaderStageInfo = new PipelineShaderStageCreateInfo[stages.Count];

            using var marshal = new StringMarshal();
            using (OptickMacros.Event("Pipeline shader modules"))
            {
                for (int i = 0; i < stages.Count; i++)
                {
                    var stage = stages[i];
                    if (shaders[stage] is VulkanShader shader)
                    {
                        shaderStageInfo[i] = VulkanUtilities.Init<PipelineShaderStageCreateInfo>() with
                        {
                            Flags = PipelineShaderStageCreateFlags.None,
                            Stage = ConvertStage(stage),
                            Module = shader.Module,
                            PName = marshal.MarshalString(shader.Entrypoint)
                        };
                    }
                    else
                    {
                        throw new ArgumentException($"The passed {stage.ToString().ToLower()} shader is not a Vulkan shader!");
                    }
                }
            }

            var reflectionData = mReflectionView!.ReflectionData;
            var vertexReflectionData = reflectionData[ShaderStage.Vertex];

            var inputs = vertexReflectionData.StageIO.Where(field => field.Direction == StageIODirection.In).ToList();
            inputs.Sort((a, b) => a.Location.CompareTo(b.Location));

            int vertexSize = 0;
            var attributes = new VertexInputAttributeDescription[inputs.Count];

            using (OptickMacros.Event("Pipeline vertex input attributes"))
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    var type = vertexReflectionData.Types[input.Type];

                    attributes[i] = VulkanUtilities.Init<VertexInputAttributeDescription>() with
                    {
                        Location = (uint)input.Location,
                        Binding = 0,
                        Format = GetAttributeFormat(type),
                        Offset = (uint)vertexSize
                    };

                    vertexSize += type.TotalSize;
                }
            }

            var spec = mDesc.Specification;
            var frontFace = spec?.FrontFace ?? default;
            var blendMode = spec?.BlendMode ?? default;
            var enableDepthTesting = spec?.EnableDepthTesting ?? default;
            bool disableCulling = spec?.DisableCulling ?? default;

            var dynamicStates = new DynamicState[]
            {
                DynamicState.Viewport,
                DynamicState.Scissor
            };

            fixed (DynamicState* dynamicStatePtr = dynamicStates)
            {
                fixed (VertexInputAttributeDescription* attributePtr = attributes)
                {
                    fixed (PipelineShaderStageCreateInfo* stagePtr = shaderStageInfo)
                    {
                        var binding = VulkanUtilities.Init<VertexInputBindingDescription>() with
                        {
                            Binding = 0,
                            Stride = (uint)vertexSize,
                            InputRate = VertexInputRate.Vertex
                        };

                        var vertexInput = VulkanUtilities.Init<PipelineVertexInputStateCreateInfo>() with
                        {
                            VertexBindingDescriptionCount = 1,
                            PVertexBindingDescriptions = &binding,
                            VertexAttributeDescriptionCount = (uint)attributes.Length,
                            PVertexAttributeDescriptions = attributePtr
                        };

                        var inputAssembly = VulkanUtilities.Init<PipelineInputAssemblyStateCreateInfo>() with
                        {
                            Topology = PrimitiveTopology.TriangleList,
                            PrimitiveRestartEnable = false
                        };

                        var viewportState = VulkanUtilities.Init<PipelineViewportStateCreateInfo>() with
                        {
                            ViewportCount = 1,
                            PViewports = null,
                            ScissorCount = 1,
                            PScissors = null
                        };

                        var rasterization = VulkanUtilities.Init<PipelineRasterizationStateCreateInfo>() with
                        {
                            DepthClampEnable = false,
                            RasterizerDiscardEnable = false,
                            PolygonMode = PolygonMode.Fill,
                            CullMode = disableCulling ? CullModeFlags.None : CullModeFlags.BackBit,
                            FrontFace = frontFace switch
                            {
                                PipelineFrontFace.Clockwise => FrontFace.Clockwise,
                                PipelineFrontFace.CounterClockwise => FrontFace.CounterClockwise,
                                _ => throw new ArgumentException($"Invalid front face: {frontFace}")
                            },
                            DepthBiasEnable = false,
                            LineWidth = 1f
                        };

                        var multisampling = VulkanUtilities.Init<PipelineMultisampleStateCreateInfo>() with
                        {
                            RasterizationSamples = SampleCountFlags.Count1Bit,
                            SampleShadingEnable = false,
                            MinSampleShading = 0f,
                            PSampleMask = null,
                            AlphaToCoverageEnable = false,
                            AlphaToOneEnable = false
                        };

                        var depthStencil = VulkanUtilities.Init<PipelineDepthStencilStateCreateInfo>() with
                        {
                            DepthTestEnable = enableDepthTesting,
                            DepthWriteEnable = enableDepthTesting,
                            DepthCompareOp = CompareOp.LessOrEqual,

                            DepthBoundsTestEnable = false,
                            StencilTestEnable = false,
                        };

                        var colorBlendAttachment = VulkanUtilities.Init<PipelineColorBlendAttachmentState>() with
                        {
                            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                        };

                        using (OptickMacros.Event("Pipeline color blend settings"))
                        {
                            if (blendMode != PipelineBlendMode.None)
                            {
                                colorBlendAttachment.BlendEnable = true;
                                colorBlendAttachment.ColorBlendOp = BlendOp.Add;
                                colorBlendAttachment.AlphaBlendOp = BlendOp.Add;

                                switch (blendMode)
                                {
                                    case PipelineBlendMode.Default:
                                        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                                        colorBlendAttachment.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;

                                        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.SrcAlpha;
                                        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                                        break;
                                    case PipelineBlendMode.Additive:
                                        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.One;
                                        colorBlendAttachment.DstColorBlendFactor = BlendFactor.One;

                                        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
                                        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.One;
                                        break;
                                    case PipelineBlendMode.OneZero:
                                        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.One;
                                        colorBlendAttachment.DstColorBlendFactor = BlendFactor.Zero;

                                        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
                                        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.Zero;
                                        break;
                                    case PipelineBlendMode.ZeroSourceColor:
                                        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.Zero;
                                        colorBlendAttachment.DstColorBlendFactor = BlendFactor.SrcColor;

                                        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.Zero;
                                        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.SrcColor;
                                        break;
                                    default:
                                        throw new ArgumentException("Unsupported blend mode!");
                                }
                            }
                        }

                        var colorBlending = VulkanUtilities.Init<PipelineColorBlendStateCreateInfo>() with
                        {
                            LogicOpEnable = false,
                            AttachmentCount = 1,
                            PAttachments = &colorBlendAttachment
                        };

                        var dynamicState = VulkanUtilities.Init<PipelineDynamicStateCreateInfo>() with
                        {
                            DynamicStateCount = (uint)dynamicStates.Length,
                            PDynamicStates = dynamicStatePtr
                        };

                        var createInfo = VulkanUtilities.Init<GraphicsPipelineCreateInfo>() with
                        {
                            Flags = PipelineCreateFlags.None,
                            StageCount = (uint)stages.Count,
                            PStages = stagePtr,
                            PVertexInputState = &vertexInput,
                            PInputAssemblyState = &inputAssembly,
                            PTessellationState = null,
                            PViewportState = &viewportState,
                            PRasterizationState = &rasterization,
                            PMultisampleState = &multisampling,
                            PDepthStencilState = &depthStencil,
                            PColorBlendState = &colorBlending,
                            PDynamicState = &dynamicState,
                            Layout = mLayout,
                            RenderPass = ((VulkanRenderPass)mDesc.RenderTarget).RenderPass,
                            Subpass = 0,
                            BasePipelineHandle = default,
                            BasePipelineIndex = 0
                        };

                        using (OptickMacros.Event("Pipeline creation"))
                        {
                            fixed (Pipeline* pipeline = &mPipeline)
                            {
                                var api = VulkanContext.API;
                                api.CreateGraphicsPipelines(mDevice.Device, default, 1, &createInfo, null, pipeline).Assert();
                            }
                        }
                    }
                }
            }
        }

        private unsafe void CreateComputePipeline(IShader shader)
        {
            using var createEvent = OptickMacros.Event();

            using var marshal = new StringMarshal();
            if (shader is VulkanShader vulkanShader)
            {
                var createInfo = VulkanUtilities.Init<ComputePipelineCreateInfo>() with
                {
                    Flags = PipelineCreateFlags.None,
                    Stage = VulkanUtilities.Init<PipelineShaderStageCreateInfo>() with
                    {
                        Flags = PipelineShaderStageCreateFlags.None,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = vulkanShader.Module,
                        PName = marshal.MarshalString(vulkanShader.Entrypoint)
                    },
                    Layout = mLayout,
                    BasePipelineHandle = default,
                    BasePipelineIndex = 0
                };

                fixed (Pipeline* pipeline = &mPipeline)
                {
                    var api = VulkanContext.API;
                    api.CreateComputePipelines(mDevice.Device, default, 1, &createInfo, null, pipeline).Assert();
                }
            }
            else
            {
                throw new ArgumentException("The passed compute shader is not a Vulkan shader!");
            }
        }

        public PipelineDescription Description => mDesc;
        public ulong ID => mID;
        public VulkanReflectionView ReflectionView => mReflectionView!;

        IReflectionView IPipeline.ReflectionView => ReflectionView;

        private readonly PipelineDescription mDesc;
        private VulkanReflectionView? mReflectionView;
        private readonly Dictionary<string, int> mPushConstantBufferOffsets;

        private Pipeline mPipeline;
        private PipelineLayout mLayout;

        private readonly Dictionary<string, ReflectionCache> mReflectionCache;
        private readonly Dictionary<nint, DynamicID> mDynamicIDs;
        private readonly Dictionary<int, DescriptorSetPipelineResources> mBoundResources;
        private readonly Dictionary<int, VulkanPipelineDescriptorSet> mDescriptorSets;
        private readonly DescriptorPool mDescriptorPool;

        private readonly VulkanDevice mDevice;
        private readonly IShaderCompiler mCompiler;
        private readonly ulong mID;
        private nint mCurrentDynamicID;
        private bool mLoaded, mDisposed;
    }
}
