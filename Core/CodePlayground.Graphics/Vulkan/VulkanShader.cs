using Optick.NET;
using shaderc;
using Silk.NET.Vulkan;
using Spirzza.Interop.SpirvCross;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using static Spirzza.Interop.SpirvCross.SpirvCross;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanShader : IShader
    {
        private static readonly IReadOnlyDictionary<spvc_resource_type, ShaderResourceTypeFlags> sResourceTypeMap;
        private static readonly IReadOnlyDictionary<spvc_resource_type, StageIODirection> sResourceTypeIODirections;
        static VulkanShader()
        {
            sResourceTypeMap = new Dictionary<spvc_resource_type, ShaderResourceTypeFlags>
            {
                [spvc_resource_type.SPVC_RESOURCE_TYPE_SAMPLED_IMAGE] = ShaderResourceTypeFlags.Image | ShaderResourceTypeFlags.Sampler,
                [spvc_resource_type.SPVC_RESOURCE_TYPE_SEPARATE_IMAGE] = ShaderResourceTypeFlags.Image,
                [spvc_resource_type.SPVC_RESOURCE_TYPE_SEPARATE_SAMPLERS] = ShaderResourceTypeFlags.Sampler,
                [spvc_resource_type.SPVC_RESOURCE_TYPE_UNIFORM_BUFFER] = ShaderResourceTypeFlags.UniformBuffer,
                [spvc_resource_type.SPVC_RESOURCE_TYPE_STORAGE_BUFFER] = ShaderResourceTypeFlags.StorageBuffer
            };

            sResourceTypeIODirections = new Dictionary<spvc_resource_type, StageIODirection>
            {
                [spvc_resource_type.SPVC_RESOURCE_TYPE_STAGE_INPUT] = StageIODirection.In,
                [spvc_resource_type.SPVC_RESOURCE_TYPE_STAGE_OUTPUT] = StageIODirection.Out
            };
        }

        public VulkanShader(VulkanDevice device, IReadOnlyList<byte> data, ShaderStage stage, string entrypoint)
        {
            mDevice = device;
            mStage = stage;
            mEntrypoint = entrypoint;
            mSPIRV = data.ToArray();
            mDisposed = false;

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

            spvc_context* context;
            spvc_parsed_ir* ir;
            spvc_compiler* compiler;
            spvc_resources* resources;

            using (OptickMacros.Event("Shader SPIRV reflection"))
            {
                spvc_context_create(&context).Assert();

                fixed (byte* spirv = mSPIRV)
                {
                    spvc_context_parse_spirv(context, (SpvId*)spirv, (nuint)(mSPIRV.Length / sizeof(SpvId)), &ir).Assert();
                }

                spvc_context_create_compiler(context, spvc_backend.SPVC_BACKEND_GLSL, ir, spvc_capture_mode.SPVC_CAPTURE_MODE_COPY, &compiler).Assert();
                spvc_compiler_create_shader_resources(compiler, &resources).Assert();
            }

            using (OptickMacros.Event("Shader push constant buffer reflection"))
            {
                spvc_reflected_resource* resourceList;
                nuint resourceCount;
                spvc_resources_get_resource_list_for_type(resources, spvc_resource_type.SPVC_RESOURCE_TYPE_PUSH_CONSTANT, &resourceList, &resourceCount).Assert();

                ProcessPushConstantBuffers(compiler, resourceList, resourceCount);
            }

            using (OptickMacros.Event("Shader resource reflection"))
            {
                foreach (var resourceType in sResourceTypeMap.Keys)
                {
                    spvc_reflected_resource* resourceList;
                    nuint resourceCount;
                    spvc_resources_get_resource_list_for_type(resources, resourceType, &resourceList, &resourceCount).Assert();

                    var typeFlags = sResourceTypeMap[resourceType];
                    ProcessResources(compiler, typeFlags, resourceList, resourceCount);
                }
            }

            using (OptickMacros.Event("Shader I/O reflection"))
            {
                foreach (var resourceType in sResourceTypeIODirections.Keys)
                {
                    spvc_reflected_resource* resourceList;
                    nuint resourceCount;
                    spvc_resources_get_resource_list_for_type(resources, resourceType, &resourceList, &resourceCount).Assert();

                    var direction = sResourceTypeIODirections[resourceType];
                    ProcessStageIOFields(compiler, direction, resourceList, resourceCount);
                }
            }

            spvc_context_destroy(context);
        }

        private unsafe void ProcessStageIOFields(spvc_compiler* compiler, StageIODirection direction, spvc_reflected_resource* resources, nuint count)
        {
            using var processEvent = OptickMacros.Event();
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var id = resource.id.Value;

                var type = resource.type_id.Value;
                ProcessType(compiler, type, resource.base_type_id.Value);

                var location = (int)spvc_compiler_get_decoration(compiler, id, SpvDecoration.SpvDecorationLocation);
                mReflectionData.StageIO.Add(new ReflectedStageIOField
                {
                    Name = Marshal.PtrToStringAnsi((nint)resource.name) ?? string.Empty,
                    Type = (int)type,
                    Direction = direction,
                    Location = location
                });
            }
        }

        private unsafe void ProcessResources(spvc_compiler* compiler, ShaderResourceTypeFlags typeFlags, spvc_reflected_resource* resources, nuint count)
        {
            using var processEvent = OptickMacros.Event();
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var id = resource.id.Value;

                var set = (int)spvc_compiler_get_decoration(compiler, id, SpvDecoration.SpvDecorationDescriptorSet);
                var binding = (int)spvc_compiler_get_decoration(compiler, id, SpvDecoration.SpvDecorationBinding);

                if (!mReflectionData.Resources.ContainsKey(set))
                {
                    mReflectionData.Resources.Add(set, new Dictionary<int, ReflectedShaderResource>());
                }

                var bindings = mReflectionData.Resources[set];
                if (bindings.ContainsKey(binding))
                {
                    var existingResource = bindings[binding];
                    existingResource.ResourceType |= typeFlags;
                    bindings[binding] = existingResource;

                    continue;
                }

                var type = resource.type_id.Value;
                ProcessType(compiler, type, resource.base_type_id.Value);

                var name = spvc_compiler_get_name(compiler, id);
                bindings.Add(binding, new ReflectedShaderResource
                {
                    Name = Marshal.PtrToStringAnsi((nint)name) ?? string.Empty,
                    Type = (int)type,
                    ResourceType = typeFlags
                });
            }
        }

        private unsafe void ProcessPushConstantBuffers(spvc_compiler* compiler, spvc_reflected_resource* resources, nuint count)
        {
            using var processEvent = OptickMacros.Event();
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var type = resource.type_id.Value;
                ProcessType(compiler, type, resource.base_type_id.Value);

                var id = resource.id.Value;
                var name = spvc_compiler_get_name(compiler, id);

                mReflectionData.PushConstantBuffers.Add(new ReflectedPushConstantBuffer
                {
                    Type = (int)type,
                    Name = Marshal.PtrToStringAnsi((nint)name) ?? string.Empty
                });
            }
        }

        private unsafe void ProcessType(spvc_compiler* compiler, uint type, uint? baseType)
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

            var handle = spvc_compiler_get_type_handle(compiler, type);

            var typeName = spvc_compiler_get_name(compiler, baseType ?? type);
            var typeDesc = new ReflectedShaderType
            {
                Name = Marshal.PtrToStringAnsi((nint)typeName) ?? string.Empty,
                ArrayDimensions = null,
                Fields = null,
                Rows = (int)spvc_type_get_vector_size(handle),
                Columns = (int)spvc_type_get_columns(handle)
            };

            var dimensions = spvc_type_get_num_array_dimensions(handle);
            if (dimensions > 0)
            {
                var arrayDimensions = new int[dimensions];
                for (uint i = 0; i < dimensions; i++)
                {
                    arrayDimensions[i] = (int)spvc_type_get_array_dimension(handle, i).Value;
                }

                typeDesc.ArrayDimensions = arrayDimensions;
            }

            var basetype = spvc_type_get_basetype(handle);
            switch (basetype)
            {
                case spvc_basetype.SPVC_BASETYPE_BOOLEAN:
                    typeDesc.Class = ShaderTypeClass.Bool;
                    typeDesc.Size = 1;
                    break;
                case spvc_basetype.SPVC_BASETYPE_INT8:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 1;
                    break;
                case spvc_basetype.SPVC_BASETYPE_UINT8:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 1;
                    break;
                case spvc_basetype.SPVC_BASETYPE_INT16:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 2;
                    break;
                case spvc_basetype.SPVC_BASETYPE_UINT16:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 2;
                    break;
                case spvc_basetype.SPVC_BASETYPE_INT32:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 4;
                    break;
                case spvc_basetype.SPVC_BASETYPE_UINT32:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 4;
                    break;
                case spvc_basetype.SPVC_BASETYPE_INT64:
                    typeDesc.Class = ShaderTypeClass.SInt;
                    typeDesc.Size = 8;
                    break;
                case spvc_basetype.SPVC_BASETYPE_UINT64:
                    typeDesc.Class = ShaderTypeClass.UInt;
                    typeDesc.Size = 8;
                    break;
                case spvc_basetype.SPVC_BASETYPE_FP16:
                    typeDesc.Class = ShaderTypeClass.Float;
                    typeDesc.Size = 2;
                    break;
                case spvc_basetype.SPVC_BASETYPE_FP32:
                    typeDesc.Class = ShaderTypeClass.Float;
                    typeDesc.Size = 4;
                    break;
                case spvc_basetype.SPVC_BASETYPE_FP64:
                    typeDesc.Class = ShaderTypeClass.Float;
                    typeDesc.Size = 8;
                    break;
                case spvc_basetype.SPVC_BASETYPE_STRUCT:
                    {
                        nuint size;
                        spvc_compiler_get_declared_struct_size(compiler, handle, &size).Assert();

                        var memberCount = spvc_type_get_num_member_types(handle);
                        var fields = new Dictionary<string, ReflectedShaderField>();

                        for (uint i = 0; i < memberCount; i++)
                        {
                            var memberTypeId = spvc_type_get_member_type(handle, i);
                            ProcessType(compiler, memberTypeId.Value, null);

                            var memberName = spvc_compiler_get_member_name(compiler, baseType ?? type, i);
                            var key = Marshal.PtrToStringAnsi((nint)memberName);
                            if (string.IsNullOrEmpty(key))
                            {
                                key = $"field_{i}";
                            }

                            uint offset;
                            spvc_compiler_type_struct_member_offset(compiler, handle, i, &offset).Assert();

                            uint stride;
                            bool hasStride = spvc_compiler_type_struct_member_array_stride(compiler, handle, i, &stride) == spvc_result.SPVC_SUCCESS;

                            fields.Add(key, new ReflectedShaderField
                            {
                                Type = (int)memberTypeId.Value,
                                Offset = (int)offset,
                                Stride = hasStride ? (int)stride : -1
                            });
                        }

                        typeDesc.Class = ShaderTypeClass.Struct;
                        typeDesc.Size = (int)size;
                        typeDesc.Fields = fields;
                    }
                    break;
                case spvc_basetype.SPVC_BASETYPE_IMAGE:
                    typeDesc.Class = ShaderTypeClass.Image;
                    typeDesc.Size = -1;
                    break;
                case spvc_basetype.SPVC_BASETYPE_SAMPLED_IMAGE:
                    typeDesc.Class = ShaderTypeClass.CombinedImageSampler;
                    typeDesc.Size = -1;
                    break;
                case spvc_basetype.SPVC_BASETYPE_SAMPLER:
                    typeDesc.Class = ShaderTypeClass.Sampler;
                    typeDesc.Size = -1;
                    break;
                default:
                    throw new InvalidOperationException($"Unimplemented base type: {basetype}");
            }

            mReflectionData.Types[typeId] = typeDesc;
        }

        #endregion

        public ShaderStage Stage => mStage;
        public string Entrypoint => mEntrypoint;
        public IReadOnlyList<byte> Bytecode => mSPIRV;
        public ShaderModule Module => mModule;
        public ShaderReflectionResult ReflectionData => mReflectionData;

        private readonly VulkanDevice mDevice;
        private ShaderModule mModule;
        private ShaderReflectionResult mReflectionData;

        private readonly ShaderStage mStage;
        private readonly string mEntrypoint;
        private readonly byte[] mSPIRV;
        private bool mDisposed;
    }

    public sealed class VulkanReflectionView : IReflectionView
    {
        public VulkanReflectionView(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            mReflectionData = new Dictionary<ShaderStage, ShaderReflectionResult>();
            foreach (var stage in shaders.Keys)
            {
                mReflectionData.Add(stage, shaders[stage].ReflectionData);
            }
        }

        public void ProcessPushConstantBuffers(out int size, out ShaderStageFlags stages)
        {
            size = 0;
            stages = ShaderStageFlags.None;

            var processedBuffers = new HashSet<string>();
            foreach (var stage in mReflectionData.Keys)
            {
                var data = mReflectionData[stage];
                if (data.PushConstantBuffers.Count == 0)
                {
                    continue;
                }

                stages |= VulkanPipeline.ConvertStage(stage);
                foreach (var pushConstantBuffer in data.PushConstantBuffers)
                {
                    if (processedBuffers.Contains(pushConstantBuffer.Name))
                    {
                        continue;
                    }

                    var typeData = data.Types[pushConstantBuffer.Type];
                    size += typeData.Size;

                    processedBuffers.Add(pushConstantBuffer.Name);
                }
            }
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

        private int GetBufferTypeID(ShaderStage stage, int set, int binding)
        {
            var reflectionData = mReflectionData[stage];
            int typeId = set < 0 ? reflectionData.PushConstantBuffers[binding].Type : reflectionData.Resources[set][binding].Type;

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
            int typeId = GetBufferTypeID(stage, set, binding);
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
            int typeId = GetBufferTypeID(stage, set, binding);
            if (typeId < 0)
            {
                return -1;
            }

            return GetTypeOffset(stage, typeId, expression);
        }

        private int GetTypeOffset(ShaderStage stage, int type, string expression)
        {
            const char beginIndexCharacter = '[';
            const char endIndexCharacter = ']';

            var reflectionData = mReflectionData[stage];
            var typeData = reflectionData.Types[type];
            var fields = typeData.Fields!;

            if (typeData.Class != ShaderTypeClass.Struct)
            {
                return -1;
            }

            int memberOperator = expression.IndexOf('.');
            string fieldExpression = memberOperator < 0 ? expression : expression[0..memberOperator];

            int beginIndexOperator = fieldExpression.IndexOf(beginIndexCharacter);
            string fieldName = beginIndexOperator < 0 ? fieldExpression : fieldExpression[0..beginIndexOperator];

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

            if (beginIndexOperator >= 0)
            {
                var fieldType = reflectionData.Types[field.Type];
                if (fieldType.ArrayDimensions is null || !fieldType.ArrayDimensions.Any())
                {
                    throw new InvalidOperationException("Cannot index a non-array field!");
                }

                int stride = field.Stride;
                if (stride <= 0)
                {
                    throw new InvalidOperationException("SPIRV-Cross did not provide a stride value!");
                }

                var indexStrings = new List<string>();
                for (int i = beginIndexOperator; i < fieldExpression.Length; i++)
                {
                    char character = fieldExpression[i];
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

                for (int i = 0; i < indexStrings.Count; i++)
                {
                    var indexString = indexStrings[i];
                    if (indexString[^1] != endIndexCharacter)
                    {
                        throw new InvalidOperationException("Malformed index operator!");
                    }

                    int index = int.Parse(indexString[1..^1]);
                    int dimensionIndex = fieldType.ArrayDimensions.Count - (i + 1);
                    int dimensionStride = stride;

                    if (index < 0 || index >= fieldType.ArrayDimensions[dimensionIndex])
                    {
                        throw new IndexOutOfRangeException();
                    }

                    for (int j = 0; j < dimensionIndex; j++)
                    {
                        dimensionStride *= fieldType.ArrayDimensions[j];
                    }

                    offset += index * dimensionStride;
                }
            }

            if (memberOperator >= 0)
            {
                offset += GetTypeOffset(stage, field.Type, expression[(memberOperator + 1)..]);
            }

            return offset;
        }

        public IReadOnlyDictionary<ShaderStage, ShaderReflectionResult> ReflectionData => mReflectionData;

        private readonly Dictionary<ShaderStage, ShaderReflectionResult> mReflectionData;
    }

    internal sealed class VulkanShaderCompiler : IShaderCompiler
    {
        public VulkanShaderCompiler(Version vulkanVersion)
        {
            mDisposed = false;
            mCompiler = new Compiler(new Options(false));

            var options = mCompiler.Options;
            uint version = VulkanUtilities.MakeVersion(vulkanVersion);

            options.SetTargetEnvironment(TargetEnvironment.Vulkan, (EnvironmentVersion)version);
            options.EnableDebugInfo();

            options.TargetSpirVVersion = new SpirVVersion(1, 0);
            options.Optimization = OptimizationLevel.Performance;
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            mCompiler.Dispose();
            mDisposed = true;
        }

        public byte[] Compile(string source, string path, ShaderLanguage language, ShaderStage stage, string entrypoint)
        {
            using var compileEvent = OptickMacros.Event();
            lock (mCompiler)
            {
                mCompiler.Options.SourceLanguage = language switch
                {
                    ShaderLanguage.GLSL => SourceLanguage.Glsl,
                    ShaderLanguage.HLSL => SourceLanguage.Hlsl,
                    _ => throw new ArgumentException("Invalid shader language!")
                };

                var kind = stage switch
                {
                    ShaderStage.Vertex => ShaderKind.VertexShader,
                    ShaderStage.Fragment => ShaderKind.FragmentShader,
                    ShaderStage.Geometry => ShaderKind.GeometryShader,
                    ShaderStage.Compute => ShaderKind.ComputeShader,
                    _ => throw new ArgumentException("Unsupported shader stage!")
                };

                using var result = mCompiler.Compile(source, path, kind, entry_point: entrypoint);
                if (result.Status != Status.Success)
                {
                    throw new InvalidOperationException($"Failed to compile shader ({result.Status}): {result.ErrorMessage}");
                }

                var spirv = new byte[result.CodeLength];
                Marshal.Copy(result.CodePointer, spirv, 0, spirv.Length);

                return spirv;
            }
        }

        public ShaderLanguage PreferredLanguage => ShaderLanguage.GLSL;

        private readonly Compiler mCompiler;
        private bool mDisposed;
    }
}
