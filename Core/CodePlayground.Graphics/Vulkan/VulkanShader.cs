using shaderc;
using Silk.NET.Vulkan;
using Spirzza.Interop.SpirvCross;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using static Spirzza.Interop.SpirvCross.SpirvCross;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanShader : IShader
    {
        public VulkanShader(VulkanDevice device, IReadOnlyList<byte> data, ShaderStage stage, string entrypoint)
        {
            mDevice = device;
            mStage = stage;
            mEntrypoint = entrypoint;
            mSPIRV = data.ToArray();
            mDisposed = false;

            Load();
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

        public ShaderStage Stage => mStage;
        public string Entrypoint => mEntrypoint;
        public IReadOnlyList<byte> Bytecode => mSPIRV;
        public ShaderModule Module => mModule;

        private readonly VulkanDevice mDevice;
        private ShaderModule mModule;

        private readonly ShaderStage mStage;
        private readonly string mEntrypoint;
        private readonly byte[] mSPIRV;
        private bool mDisposed;
    }

    internal sealed class VulkanShaderCompiler : IShaderCompiler
    {
        private static readonly IReadOnlyDictionary<spvc_resource_type, ShaderResourceTypeFlags> sResourceTypeMap;
        private static readonly IReadOnlyDictionary<spvc_resource_type, StageIODirection> sResourceTypeIODirections;
        static VulkanShaderCompiler()
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

        public unsafe ShaderReflectionResult Reflect(IReadOnlyList<byte> bytecode)
        {
            mReflectionResult = new ShaderReflectionResult
            {
                StageIO = new List<ReflectedStageIOField>(),
                Resources = new Dictionary<int, Dictionary<int, ReflectedShaderResource>>(),
                Types = new Dictionary<int, ReflectedShaderType>()
            };

            spvc_context* context;
            spvc_context_create(&context).Assert();

            spvc_parsed_ir* ir;
            var array = bytecode.ToArray();
            fixed (byte* spirv = array)
            {
                spvc_context_parse_spirv(context, (SpvId*)spirv, (nuint)(bytecode.Count / sizeof(SpvId)), &ir).Assert();
            }

            spvc_compiler* compiler;
            spvc_context_create_compiler(context, spvc_backend.SPVC_BACKEND_GLSL, ir, spvc_capture_mode.SPVC_CAPTURE_MODE_COPY, &compiler).Assert();

            spvc_resources* resources;
            spvc_compiler_create_shader_resources(compiler, &resources).Assert();

            foreach (var resourceType in sResourceTypeMap.Keys)
            {
                spvc_reflected_resource* resourceList;
                nuint resourceCount;
                spvc_resources_get_resource_list_for_type(resources, resourceType, &resourceList, &resourceCount).Assert();

                var typeFlags = sResourceTypeMap[resourceType];
                ProcessResources(compiler, typeFlags, resourceList, resourceCount);
            }

            foreach (var resourceType in sResourceTypeIODirections.Keys)
            {
                spvc_reflected_resource* resourceList;
                nuint resourceCount;
                spvc_resources_get_resource_list_for_type(resources, resourceType, &resourceList, &resourceCount).Assert();

                var direction = sResourceTypeIODirections[resourceType];
                ProcessStageIOFields(compiler, direction, resourceList, resourceCount);
            }

            spvc_context_destroy(context);
            return mReflectionResult;
        }

        private unsafe void ProcessStageIOFields(spvc_compiler* compiler, StageIODirection direction, spvc_reflected_resource* resources, nuint count)
        {
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var id = resource.id.Value;

                var type = resource.type_id.Value;
                ProcessType(compiler, type, resource.base_type_id.Value);

                var location = (int)spvc_compiler_get_decoration(compiler, id, SpvDecoration.SpvDecorationLocation);
                mReflectionResult.StageIO.Add(new ReflectedStageIOField
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
            for (nuint i = 0; i < count; i++)
            {
                var resource = resources[i];
                var id = resource.id.Value;

                var set = (int)spvc_compiler_get_decoration(compiler, id, SpvDecoration.SpvDecorationDescriptorSet);
                var binding = (int)spvc_compiler_get_decoration(compiler, id, SpvDecoration.SpvDecorationBinding);

                if (!mReflectionResult.Resources.ContainsKey(set))
                {
                    mReflectionResult.Resources.Add(set, new Dictionary<int, ReflectedShaderResource>());
                }

                var bindings = mReflectionResult.Resources[set];
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

        private unsafe void ProcessType(spvc_compiler* compiler, uint type, uint? baseType)
        {
            int typeId = (int)type;
            if (mReflectionResult.Types.ContainsKey(typeId))
            {
                return;
            }

            // placeholder so that we don't infinitely recurse
            mReflectionResult.Types.Add(typeId, new ReflectedShaderType());

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

            mReflectionResult.Types[typeId] = typeDesc;
        }

        public ShaderLanguage PreferredLanguage => ShaderLanguage.GLSL;

        private readonly Compiler mCompiler;
        private ShaderReflectionResult mReflectionResult;
        private bool mDisposed;
    }
}
