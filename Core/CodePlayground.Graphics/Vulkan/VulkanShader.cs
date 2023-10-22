using Optick.NET;
using Silk.NET.Shaderc;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using SpvCompiler = Silk.NET.SPIRV.Cross.Compiler;
using ShadercCompiler = Silk.NET.Shaderc.Compiler;
using SourceLanguage = Silk.NET.Shaderc.SourceLanguage;
using System.Text;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanShader : IShader
    {
        private static readonly IReadOnlyDictionary<ResourceType, ShaderResourceTypeFlags> sResourceTypeMap;
        private static readonly IReadOnlyDictionary<ResourceType, StageIODirection> sResourceTypeIODirections;
        private static ulong sCurrentID;

        private static readonly Cross spvc;
        static VulkanShader()
        {
            sResourceTypeMap = new Dictionary<ResourceType, ShaderResourceTypeFlags>
            {
                [ResourceType.SampledImage] = ShaderResourceTypeFlags.Image | ShaderResourceTypeFlags.Sampler,
                [ResourceType.SeparateImage] = ShaderResourceTypeFlags.Image,
                [ResourceType.SeparateSamplers] = ShaderResourceTypeFlags.Sampler,
                [ResourceType.UniformBuffer] = ShaderResourceTypeFlags.UniformBuffer,
                [ResourceType.StorageBuffer] = ShaderResourceTypeFlags.StorageBuffer,
                [ResourceType.StorageImage] = ShaderResourceTypeFlags.StorageImage
            };

            sResourceTypeIODirections = new Dictionary<ResourceType, StageIODirection>
            {
                [ResourceType.StageInput] = StageIODirection.In,
                [ResourceType.StageOutput] = StageIODirection.Out
            };

            spvc = Cross.GetApi();
            sCurrentID = 0;
        }

        public VulkanShader(VulkanDevice device, IReadOnlyList<byte> data, ShaderStage stage, string entrypoint)
        {
            mDevice = device;
            mStage = stage;
            mEntrypoint = entrypoint;
            mSPIRV = data.ToArray();
            mDisposed = false;
            mID = sCurrentID++;

            Load();
            Reflect();
        }

        ~VulkanShader()
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
            api.DestroyShaderModule(mDevice.Device, mModule, null);
        }

        private unsafe void Load()
        {
            using var loadEvent = OptickMacros.Event();
            fixed (byte* ptr = mSPIRV)
            {
                var createInfo = VulkanUtilities.Init<ShaderModuleCreateInfo>() with
                {
                    Flags = ShaderModuleCreateFlags.None,
                    CodeSize = (nuint)mSPIRV.Length,
                    PCode = (uint*)ptr
                };

                fixed (ShaderModule* module = &mModule)
                {
                    var api = VulkanContext.API;
                    api.CreateShaderModule(mDevice.Device, &createInfo, null, module);
                }
            }
        }

        #region Reflection

        private unsafe void Reflect()
        {
            using var reflectEvent = OptickMacros.Event();
            mReflectionData = new ShaderReflectionResult
            {
                StageIO = new List<ReflectedStageIOField>(),
                Resources = new Dictionary<int, Dictionary<int, ReflectedShaderResource>>(),
                PushConstantBuffers = new List<ReflectedPushConstantBuffer>(),
                Types = new Dictionary<int, ReflectedShaderType>()
            };

            Context* context = null;
            ParsedIr* ir = null;
            SpvCompiler* compiler = null;
            Resources* resources = null;

            using (OptickMacros.Event("Shader SPIRV reflection"))
            {
                spvc.ContextCreate(ref context).Assert();
                fixed (byte* spirv = mSPIRV)
                {
                    spvc.ContextParseSpirv(context, (uint*)spirv, (nuint)(mSPIRV.Length / sizeof(uint)), &ir).Assert();
                }

                spvc.ContextCreateCompiler(context, Backend.Glsl, ir, CaptureMode.Copy, ref compiler).Assert();
                spvc.CompilerCreateShaderResources(compiler, ref resources).Assert();
            }

            using (OptickMacros.Event("Shader push constant buffer reflection"))
            {
                ReflectedResource* resourceList;
                nuint resourceCount;
                spvc.ResourcesGetResourceListForType(resources, ResourceType.PushConstant, &resourceList, &resourceCount).Assert();

                ProcessPushConstantBuffers(compiler, resourceList, resourceCount);
            }

            using (OptickMacros.Event("Shader resource reflection"))
            {
                foreach (var resourceType in sResourceTypeMap.Keys)
                {
                    ReflectedResource* resourceList;
                    nuint resourceCount;
                    spvc.ResourcesGetResourceListForType(resources, resourceType, &resourceList, &resourceCount).Assert();

                    var typeFlags = sResourceTypeMap[resourceType];
                    ProcessResources(compiler, typeFlags, resourceList, resourceCount);
                }
            }

            using (OptickMacros.Event("Shader I/O reflection"))
            {
                foreach (var resourceType in sResourceTypeIODirections.Keys)
                {
                    ReflectedResource* resourceList;
                    nuint resourceCount;
                    spvc.ResourcesGetResourceListForType(resources, resourceType, &resourceList, &resourceCount).Assert();

                    var direction = sResourceTypeIODirections[resourceType];
                    ProcessStageIOFields(compiler, direction, resourceList, resourceCount);
                }
            }

            spvc.ContextDestroy(context);
        }

        private unsafe void ProcessStageIOFields(SpvCompiler* compiler, StageIODirection direction, ReflectedResource* resources, nuint count)
        {
            using var processEvent = OptickMacros.Event();
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var id = resource.Id;

                var type = resource.TypeId;
                ProcessType(compiler, type, resource.BaseTypeId);

                var location = (int)spvc.CompilerGetDecoration(compiler, id, Decoration.Location);
                mReflectionData.StageIO.Add(new ReflectedStageIOField
                {
                    Name = Marshal.PtrToStringAnsi((nint)resource.Name) ?? string.Empty,
                    Type = (int)type,
                    Direction = direction,
                    Location = location
                });
            }
        }

        private unsafe void ProcessResources(SpvCompiler* compiler, ShaderResourceTypeFlags typeFlags, ReflectedResource* resources, nuint count)
        {
            using var processEvent = OptickMacros.Event();
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var id = resource.Id;

                var set = (int)spvc.CompilerGetDecoration(compiler, id, Decoration.DescriptorSet);
                var binding = (int)spvc.CompilerGetDecoration(compiler, id, Decoration.Binding);

                mReflectionData.Resources.TryAdd(set, new Dictionary<int, ReflectedShaderResource>());

                var bindings = mReflectionData.Resources[set];
                if (bindings.TryGetValue(binding, out ReflectedShaderResource existingResource))
                {
                    existingResource.ResourceType |= typeFlags;
                    bindings[binding] = existingResource;

                    continue;
                }

                var type = resource.TypeId;
                ProcessType(compiler, type, resource.BaseTypeId);

                var name = spvc.CompilerGetName(compiler, id);
                bindings.Add(binding, new ReflectedShaderResource
                {
                    Name = Marshal.PtrToStringAnsi((nint)name) ?? string.Empty,
                    Type = (int)type,
                    ResourceType = typeFlags
                });
            }
        }

        private unsafe void ProcessPushConstantBuffers(SpvCompiler* compiler, ReflectedResource* resources, nuint count)
        {
            using var processEvent = OptickMacros.Event();
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var type = resource.TypeId;
                ProcessType(compiler, type, resource.BaseTypeId);

                var id = resource.Id;
                var name = spvc.CompilerGetName(compiler, id);

                mReflectionData.PushConstantBuffers.Add(new ReflectedPushConstantBuffer
                {
                    Type = (int)type,
                    Name = Marshal.PtrToStringAnsi((nint)name) ?? string.Empty
                });
            }
        }

        private unsafe void ProcessType(SpvCompiler* compiler, uint type, uint? baseType)
        {
            using var processEvent = OptickMacros.Event();
            OptickMacros.Tag("Processed type", type);

            int typeId = (int)type;
            if (mReflectionData.Types.ContainsKey(typeId))
            {
                return;
            }

            // placeholder so that we don't infinitely recurse
            mReflectionData.Types.Add(typeId, new ReflectedShaderType());

            var handle = spvc.CompilerGetTypeHandle(compiler, type);

            var typeName = spvc.CompilerGetName(compiler, baseType ?? type);
            var typeDesc = new ReflectedShaderType
            {
                Name = Marshal.PtrToStringAnsi((nint)typeName) ?? string.Empty,
                ArrayDimensions = null,
                Fields = null,
                Rows = (int)spvc.TypeGetVectorSize(handle),
                Columns = (int)spvc.TypeGetColumns(handle)
            };

            var dimensions = spvc.TypeGetNumArrayDimensions(handle);
            if (dimensions > 0)
            {
                var arrayDimensions = new int[dimensions];
                for (uint i = 0; i < dimensions; i++)
                {
                    arrayDimensions[i] = (int)spvc.TypeGetArrayDimension(handle, i);
                }

                typeDesc.ArrayDimensions = arrayDimensions;
            }

            var basetype = spvc.TypeGetBasetype(handle);
            switch (basetype)
            {
                case Basetype.Boolean:
                    typeDesc.Class = ShaderTypeClass.Bool;
                    typeDesc.Size = 1;
                    break;
                case Basetype.Int8:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 1;
                    break;
                case Basetype.Uint8:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 1;
                    break;
                case Basetype.Int16:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 2;
                    break;
                case Basetype.Uint16:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 2;
                    break;
                case Basetype.Int32:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 4;
                    break;
                case Basetype.Uint32:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 4;
                    break;
                case Basetype.Int64:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 8;
                    break;
                case Basetype.Uint64:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 8;
                    break;
                case Basetype.FP16:
                    typeDesc.Class = ShaderTypeClass.Float;
                    typeDesc.Size = 2;
                    break;
                case Basetype.FP32:
                    typeDesc.Class = ShaderTypeClass.Float;
                    typeDesc.Size = 4;
                    break;
                case Basetype.FP64:
                    typeDesc.Class = ShaderTypeClass.Float;
                    typeDesc.Size = 8;
                    break;
                case Basetype.Struct:
                    {
                        var baseTypeHandle = spvc.CompilerGetTypeHandle(compiler, baseType ?? type);
                        
                        nuint size;
                        spvc.CompilerGetDeclaredStructSize(compiler, handle, &size).Assert();

                        var memberCount = spvc.TypeGetNumMemberTypes(handle);
                        var fields = new Dictionary<string, ReflectedShaderField>();

                        for (uint i = 0; i < memberCount; i++)
                        {
                            var memberTypeId = spvc.TypeGetMemberType(handle, i);
                            var memberTypeHandle = spvc.CompilerGetTypeHandle(compiler, memberTypeId);
                            var memberBaseTypeId = spvc.TypeGetBaseTypeId(memberTypeHandle);
                            ProcessType(compiler, memberTypeId, null);

                            var memberName = spvc.CompilerGetMemberName(compiler, baseType ?? type, i);
                            var key = Marshal.PtrToStringAnsi((nint)memberName);
                            if (string.IsNullOrEmpty(key))
                            {
                                key = $"field_{i}";
                            }

                            uint offset;
                            spvc.CompilerTypeStructMemberOffset(compiler, handle, i, &offset).Assert();

                            uint stride;
                            bool hasStride = spvc.CompilerTypeStructMemberArrayStride(compiler, handle, i, &stride) == Silk.NET.SPIRV.Cross.Result.Success;

                            fields.Add(key, new ReflectedShaderField
                            {
                                Type = (int)memberTypeId,
                                Offset = (int)offset,
                                Stride = hasStride ? (int)stride : -1
                            });
                        }

                        typeDesc.Class = ShaderTypeClass.Struct;
                        typeDesc.Size = (int)size;
                        typeDesc.Fields = fields;
                    }
                    break;
                case Basetype.Image:
                    typeDesc.Class = ShaderTypeClass.Image;
                    typeDesc.Size = -1;
                    break;
                case Basetype.SampledImage:
                    typeDesc.Class = ShaderTypeClass.CombinedImageSampler;
                    typeDesc.Size = -1;
                    break;
                case Basetype.Sampler:
                    typeDesc.Class = ShaderTypeClass.Sampler;
                    typeDesc.Size = -1;
                    break;
                default:
                    throw new InvalidOperationException($"Unimplemented base type: {basetype}");
            }

            mReflectionData.Types[typeId] = typeDesc;
        }

        #endregion

        public ulong ID => mID;
        public ShaderStage Stage => mStage;
        public string Entrypoint => mEntrypoint;
        public IReadOnlyList<byte> Bytecode => mSPIRV;
        public ShaderModule Module => mModule;
        public ShaderReflectionResult ReflectionData => mReflectionData;

        private readonly VulkanDevice mDevice;
        private ShaderModule mModule;
        private ShaderReflectionResult mReflectionData;

        private readonly ulong mID;
        private readonly ShaderStage mStage;
        private readonly string mEntrypoint;
        private readonly byte[] mSPIRV;
        private bool mDisposed;
    }

    public struct VulkanVertexAttributeLayout
    {
        public int TotalSize;
        public VertexInputAttributeDescription[] Attributes;
    }

    public sealed class VulkanReflectionView : IReflectionView
    {
        private static readonly IReadOnlyDictionary<ShaderTypeClass, IReadOnlyDictionary<int, Format>> sAttributeFormats;
        static VulkanReflectionView()
        {
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

        public VulkanReflectionView(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            mReflectionData = new Dictionary<ShaderStage, ShaderReflectionResult>();
            foreach (var stage in shaders.Keys)
            {
                mReflectionData.Add(stage, shaders[stage].ReflectionData);
            }

            mRanges = ProcessPushConstantBuffers(out mTotalPushConstantSize, out mPushConstantStages);
        }

        private Dictionary<string, PushConstantRange> ProcessPushConstantBuffers(out int totalSize, out ShaderStageFlags stages)
        {
            using var processPushConstantsEvent = OptickMacros.Event();

            totalSize = 0;
            stages = ShaderStageFlags.None;

            var ranges = new Dictionary<string, PushConstantRange>();
            foreach (var stage in mReflectionData.Keys)
            {
                var data = mReflectionData[stage];
                if (data.PushConstantBuffers.Count == 0)
                {
                    continue;
                }

                var stageFlag = VulkanPipeline.ConvertStage(stage);
                stages |= stageFlag;

                foreach (var pushConstantBuffer in data.PushConstantBuffers)
                {
                    string name = pushConstantBuffer.Name;
                    if (ranges.TryGetValue(name, out PushConstantRange range))
                    {
                        range.StageFlags |= stageFlag;
                        ranges[name] = range;
                    }
                    else
                    {
                        var typeData = data.Types[pushConstantBuffer.Type];
                        ranges.Add(name, new PushConstantRange
                        {
                            StageFlags = stageFlag,
                            Offset = (uint)totalSize,
                            Size = (uint)typeData.Size
                        });

                        totalSize += typeData.Size;
                    }
                }
            }

            return ranges;
        }

        public int GetDescriptorSetBindingCount(int set)
        {
            int setBindingCount = 0;
            foreach (var stage in mReflectionData.Keys)
            {
                var data = mReflectionData[stage];
                if (data.Resources.TryGetValue(set, out Dictionary<int, ReflectedShaderResource>? bindings))
                {
                    setBindingCount += bindings.Count;
                }
            }

            return setBindingCount;
        }

        public bool FindResource(string name, out ShaderStage stage, out int set, out int binding)
        {
            foreach (var currentStage in mReflectionData.Keys)
            {
                var data = mReflectionData[currentStage];
                for (int i = 0; i < data.PushConstantBuffers.Count; i++)
                {
                    var buffer = data.PushConstantBuffers[i];
                    if (buffer.Name == name)
                    {
                        stage = currentStage;
                        set = -1;
                        binding = i;

                        return true;
                    }
                }

                foreach (int currentSet in data.Resources.Keys)
                {
                    var setResources = data.Resources[currentSet];
                    foreach (int currentBinding in setResources.Keys)
                    {
                        var currentResource = setResources[currentBinding];
                        if (currentResource.Name == name)
                        {
                            stage = currentStage;
                            set = currentSet;
                            binding = currentBinding;

                            return true;
                        }
                    }
                }
            }

            stage = default;
            set = binding = -1;
            return false;
        }

        public bool ResourceExists(string resource)
        {
            return FindResource(resource, out _, out _, out _);
        }

        private int GetBufferTypeID(ShaderStage stage, int set, int binding, out bool pushConstant)
        {
            var reflectionData = mReflectionData[stage];
            int typeId = (pushConstant = set < 0) ? reflectionData.PushConstantBuffers[binding].Type : reflectionData.Resources[set][binding].Type;

            var typeData = reflectionData.Types[typeId];
            return typeData.Class != ShaderTypeClass.Struct ? -1 : typeId;
        }

        public int GetBufferSize(string resource)
        {
            if (!FindResource(resource, out ShaderStage stage, out int set, out int binding))
            {
                return -1;
            }

            return GetBufferSize(stage, set, binding);
        }

        public int GetBufferSize(ShaderStage stage, int set, int binding)
        {
            int typeId = GetBufferTypeID(stage, set, binding, out _);
            if (typeId < 0)
            {
                return -1;
            }

            var typeData = mReflectionData[stage].Types[typeId];
            return typeData.Size;
        }

        public int GetBufferOffset(string resource, string expression)
        {
            if (!FindResource(resource, out ShaderStage stage, out int set, out int binding))
            {
                return -1;
            }

            return GetBufferOffset(stage, set, binding, expression);
        }

        public int GetBufferOffset(ShaderStage stage, int set, int binding, string expression)
        {
            int typeId = GetBufferTypeID(stage, set, binding, out bool pushConstant);
            if (typeId < 0)
            {
                return -1;
            }

            int baseOffset = 0;
            if (pushConstant)
            {
                string name = mReflectionData[stage].PushConstantBuffers[binding].Name;
                baseOffset = (int)mRanges[name].Offset;
            }

            return baseOffset + GetTypeOffset(stage, typeId, expression);
        }

        public static Format GetAttributeFormat(ReflectedShaderType attributeType)
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

        object IReflectionView.CreateVertexAttributeLayout() => CreateVertexAttributeLayout();
        public VulkanVertexAttributeLayout CreateVertexAttributeLayout()
        {
            using var createLayoutEvent = OptickMacros.Event();
            var vertexReflectionData = mReflectionData[ShaderStage.Vertex];

            var inputs = vertexReflectionData.StageIO.Where(field => field.Direction == StageIODirection.In).ToList();
            inputs.Sort((a, b) => a.Location.CompareTo(b.Location));

            int vertexSize = 0;
            var attributes = new VertexInputAttributeDescription[inputs.Count];

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

            return new VulkanVertexAttributeLayout
            {
                TotalSize = vertexSize,
                Attributes = attributes
            };
        }

        internal static int[] ParseFieldExpression(string expression, out string fieldName)
        {
            const char beginIndexCharacter = '[';
            const char endIndexCharacter = ']';

            int beginIndexOperator = expression.IndexOf(beginIndexCharacter);
            fieldName = beginIndexOperator < 0 ? expression : expression[0..beginIndexOperator];

            if (string.IsNullOrEmpty(fieldName) || beginIndexOperator < 0)
            {
                return Array.Empty<int>();
            }

            var indexStrings = new List<string>();
            for (int i = beginIndexOperator; i < expression.Length; i++)
            {
                char character = expression[i];
                if (character == beginIndexCharacter)
                {
                    indexStrings.Add(beginIndexCharacter.ToString());
                }
                else
                {
                    var currentString = indexStrings[^1];
                    if (currentString[^1] == endIndexCharacter)
                    {
                        throw new InvalidOperationException("Malformed index operator!");
                    }

                    indexStrings[^1] = currentString + character;
                }
            }

            var indices = new int[indexStrings.Count];
            for (int i = 0; i < indexStrings.Count; i++)
            {
                var indexString = indexStrings[i];
                if (indexString[^1] != endIndexCharacter)
                {
                    throw new InvalidOperationException("Malformed index operator!");
                }

                indices[i] = int.Parse(indexString[1..^1]);
            }

            return indices;
        }

        internal static int GetIndexedOffset(IReadOnlyList<int> indices, ReflectedShaderField field, ReflectedShaderType fieldType)
        {
            if (fieldType.ArrayDimensions is null || !fieldType.ArrayDimensions.Any())
            {
                throw new InvalidOperationException("Cannot index a non-array field!");
            }

            int stride = field.Stride;
            if (stride <= 0)
            {
                throw new InvalidOperationException("SPIRV-Cross did not provide a stride value!");
            }

            int offset = 0;
            for (int i = 0; i < indices.Count; i++)
            {
                int index = indices[i];
                int dimensionIndex = fieldType.ArrayDimensions.Count - (i + 1);
                int dimensionStride = stride;

                int dimensionSize = fieldType.ArrayDimensions[dimensionIndex];
                if (index < 0 || (dimensionSize > 0 && index >= fieldType.ArrayDimensions[dimensionIndex]))
                {
                    throw new IndexOutOfRangeException();
                }

                for (int j = 0; j < dimensionIndex; j++)
                {
                    dimensionStride *= fieldType.ArrayDimensions[j];
                }

                offset += index * dimensionStride;
            }

            return offset;
        }

        private int GetTypeOffset(ShaderStage stage, int type, string expression)
        {
            var reflectionData = mReflectionData[stage];
            var typeData = reflectionData.Types[type];
            var fields = typeData.Fields!;

            if (typeData.Class != ShaderTypeClass.Struct)
            {
                return -1;
            }

            int memberOperator = expression.IndexOf('.');
            string fieldExpression = memberOperator < 0 ? expression : expression[0..memberOperator];
            var indices = ParseFieldExpression(fieldExpression, out string fieldName);

            if (string.IsNullOrEmpty(fieldName))
            {
                throw new ArgumentException("No field name given!");
            }

            if (!fields.ContainsKey(fieldName))
            {
                return -1;
            }

            var field = fields[fieldName];
            int offset = field.Offset;

            if (indices.Length > 0)
            {
                var fieldType = reflectionData.Types[field.Type];
                offset += GetIndexedOffset(indices, field, fieldType);
            }

            if (memberOperator > 0)
            {
                int childOffset = GetTypeOffset(stage, field.Type, expression[(memberOperator + 1)..]);
                if (childOffset < 0)
                {
                    offset = -1;
                }
                else
                {
                    offset += childOffset;
                }
            }

            return offset;
        }

        IReflectionNode? IReflectionView.GetResourceNode(string resource) => GetResourceNode(resource);
        public VulkanReflectionNode? GetResourceNode(string resource)
        {
            if (!FindResource(resource, out ShaderStage stage, out int set, out int binding))
            {
                return null;
            }

            int type = GetBufferTypeID(stage, set, binding, out bool pushConstant);
            int baseOffset = 0;

            if (pushConstant)
            {
                string name = mReflectionData[stage].PushConstantBuffers[binding].Name;
                baseOffset = (int)mRanges[name].Offset;
            }

            return type < 0 ? null : new VulkanReflectionNode(this, resource, stage, type, baseOffset);
        }

        public IReadOnlyDictionary<string, PushConstantRange> PushConstantRanges => mRanges;
        public ShaderStageFlags PushConstantStages => mPushConstantStages;
        public int TotalPushConstantSize => mTotalPushConstantSize;
        public IReadOnlyDictionary<ShaderStage, ShaderReflectionResult> ReflectionData => mReflectionData;

        private readonly Dictionary<string, PushConstantRange> mRanges;
        private readonly ShaderStageFlags mPushConstantStages;
        private readonly int mTotalPushConstantSize;
        private readonly Dictionary<ShaderStage, ShaderReflectionResult> mReflectionData;
    }

    public sealed class VulkanReflectionNode : IReflectionNode
    {
        internal VulkanReflectionNode(VulkanReflectionView view, string resourceName, ShaderStage stage, int type, int offset)
        {
            mView = view;
            mResourceName = resourceName;
            mStage = stage;

            mType = type;
            mOffset = offset;
        }

        public string ResourceName => mResourceName;
        public int Offset => mOffset;
        public ReflectedShaderType TypeData => mView.ReflectionData[mStage].Types[mType];

        IReflectionNode? IReflectionNode.Find(string name) => Find(name);
        public VulkanReflectionNode? Find(string name)
        {
            try
            {
                var indices = VulkanReflectionView.ParseFieldExpression(name, out string fieldName);
                var fields = TypeData.Fields;

                if (fields is null || !fields.TryGetValue(fieldName, out ReflectedShaderField field))
                {
                    return null;
                }

                int offset = mOffset + field.Offset;
                if (indices.Length > 0)
                {
                    var fieldType = mView.ReflectionData[mStage].Types[field.Type];
                    offset += VulkanReflectionView.GetIndexedOffset(indices, field, fieldType);
                }

                return new VulkanReflectionNode(mView, mResourceName, mStage, field.Type, offset);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private readonly VulkanReflectionView mView;
        private readonly string mResourceName;
        private readonly ShaderStage mStage;

        private readonly int mType;
        private readonly int mOffset;
    }

    internal sealed unsafe class VulkanShaderCompiler : IShaderCompiler
    {
        private static readonly Shaderc shaderc;
        static VulkanShaderCompiler()
        {
            shaderc = Shaderc.GetApi();
        }

        public VulkanShaderCompiler(Version vulkanVersion)
        {
            mDisposed = false;
            mLock = new object();

            mCompiler = shaderc.CompilerInitialize();
            mOptions = shaderc.CompileOptionsInitialize();

            uint version = VulkanUtilities.MakeVersion(vulkanVersion);
            shaderc.CompileOptionsSetTargetEnv(mOptions, TargetEnv.Vulkan, version);
            shaderc.CompileOptionsSetGenerateDebugInfo(mOptions);
            shaderc.CompileOptionsSetTargetSpirv(mOptions, SpirvVersion.Shaderc10);
            shaderc.CompileOptionsSetOptimizationLevel(mOptions, OptimizationLevel.Performance);
        }

        ~VulkanShaderCompiler()
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

        private void Dispose(bool disposing)
        {
            shaderc.CompilerRelease(mCompiler);
            shaderc.CompileOptionsRelease(mOptions);
        }

        public byte[] Compile(string source, string path, ShaderLanguage language, ShaderStage stage, string entrypoint)
        {
            using var compileEvent = OptickMacros.Event();
            lock (mLock)
            {
                shaderc.CompileOptionsSetSourceLanguage(mOptions, language switch
                {
                    ShaderLanguage.GLSL => SourceLanguage.Glsl,
                    ShaderLanguage.HLSL => SourceLanguage.Hlsl,
                    _ => throw new ArgumentException("Invalid shader language!")
                });

                var kind = stage switch
                {
                    ShaderStage.Vertex => ShaderKind.VertexShader,
                    ShaderStage.Fragment => ShaderKind.FragmentShader,
                    ShaderStage.Geometry => ShaderKind.GeometryShader,
                    ShaderStage.Compute => ShaderKind.ComputeShader,
                    _ => throw new ArgumentException("Unsupported shader stage!")
                };

                var encoding = Encoding.ASCII;
                var sourceBytes = encoding.GetBytes(source);
                var pathBytes = encoding.GetBytes(path);
                var entrypointBytes = encoding.GetBytes(entrypoint);

                CompilationResult* result;
                fixed (byte* sourcePtr = sourceBytes)
                {
                    fixed (byte* pathPtr = pathBytes)
                    {
                        fixed (byte* entrypointPtr = entrypointBytes)
                        {
                            result = shaderc.CompileIntoSpv(mCompiler, sourcePtr, (nuint)sourceBytes.Length, kind, pathPtr, entrypointPtr, mOptions);
                        }
                    }
                }

                var status = shaderc.ResultGetCompilationStatus(result);
                if (status != CompilationStatus.Success)
                {
                    var message = (nint)shaderc.ResultGetErrorMessage(result);
                    throw new InvalidOperationException($"Failed to compile shader ({status}): {Marshal.PtrToStringAnsi(message)}");
                }

                var spirv = new byte[shaderc.ResultGetLength(result)];
                Marshal.Copy((nint)shaderc.ResultGetBytes(result), spirv, 0, spirv.Length);

                shaderc.ResultRelease(result);
                return spirv;
            }
        }

        public ShaderLanguage PreferredLanguage => ShaderLanguage.GLSL;

        private readonly ShadercCompiler* mCompiler;
        private readonly CompileOptions* mOptions;
        private readonly object mLock;
        private bool mDisposed;
    }
}
