using System;

namespace CodePlayground.Graphics.Shaders
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class CompiledShaderAttribute : Attribute
    {
        public CompiledShaderAttribute()
        {
            ID = string.Empty;
        }

        public string ID { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ShaderEntrypointAttribute : Attribute
    {
        public ShaderEntrypointAttribute(ShaderStage stage)
        {
            Stage = stage;
        }

        public ShaderStage Stage { get; }
    }

    public enum PrimitiveShaderTypeClass
    {
        Value,
        Sampler,
        Image
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class PrimitiveShaderTypeAttribute : Attribute
    {
        public PrimitiveShaderTypeAttribute(string name)
        {
            Name = name;
            Instantiable = true;
            TypeClass = PrimitiveShaderTypeClass.Value;
        }

        public string Name { get; }
        public bool Instantiable { get; set; }
        public PrimitiveShaderTypeClass TypeClass { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class BuiltinShaderFunctionAttribute : Attribute
    {
        public BuiltinShaderFunctionAttribute(string name)
        {
            Name = name;
            Keyword = false;
        }

        public string Name { get; }
        public bool Keyword { get; set; }
    }

    public enum ShaderOperatorType
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Invert,
        Index
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class ShaderOperatorAttribute : Attribute
    {
        public ShaderOperatorAttribute(ShaderOperatorType type)
        {
            Type = type;
        }

        public ShaderOperatorType Type { get; }
    }

    public enum ShaderResourceType
    {
        Uniform,
        [ShaderFieldName("buffer")]
        StorageBuffer
    }

    public enum ShaderImageFormat
    {
        // todo: add more image formats
        R8,
        RG8,
        RGBA8,

        R16,
        RG16,
        RGBA16,
    }

    [AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Parameter | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class LayoutAttribute : Attribute
    {
        public LayoutAttribute()
        {
            Location = -1;
            Set = Binding = 0;
            ResourceType = ShaderResourceType.Uniform;
            Format = ShaderImageFormat.RGBA8;
            PushConstants = false;
        }

        public int Location { get; set; }
        public int Set { get; set; }
        public int Binding { get; set; }
        public ShaderResourceType ResourceType { get; set; }
        public ShaderImageFormat Format { get; set; }
        public bool PushConstants { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class ShaderFieldNameAttribute : Attribute
    {
        public ShaderFieldNameAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    public enum ShaderVariableID
    {
        // vertex shader
        OutputPosition,

        // compute shader
        WorkGroupCount,
        WorkGroupID,
        WorkGroupSize,
        LocalInvocationID,
        GlobalInvocationID,
        LocalInvocationIndex
    }

    [AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
    public sealed class ShaderVariableAttribute : Attribute
    {
        public ShaderVariableAttribute(ShaderVariableID id)
        {
            ID = id;
        }

        public ShaderVariableID ID { get; }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class ArraySizeAttribute : Attribute
    {
        public ArraySizeAttribute(uint length)
        {
            Length = length;
        }

        public uint Length { get; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class NumThreadsAttribute : Attribute
    {
        public NumThreadsAttribute(uint x, uint y, uint z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public uint X { get; }
        public uint Y { get; }
        public uint Z { get; }
    }
}