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
        public PrimitiveShaderTypeAttribute()
        {
            Instantiable = true;
            TypeClass = PrimitiveShaderTypeClass.Value;
        }

        public bool Instantiable { get; set; }
        public PrimitiveShaderTypeClass TypeClass { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
    public sealed class NamedShaderSymbolAttribute : Attribute
    {
        public NamedShaderSymbolAttribute(string name)
        {
            Name = name;
            Language = ShaderLanguage.None;
        }

        public ShaderLanguage Language { get; set; }
        public string Name { get; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class BuiltinShaderFunctionAttribute : Attribute
    {
        public BuiltinShaderFunctionAttribute()
        {
            Keyword = false;
        }

        public bool Keyword { get; set; }
    }

    public enum ShaderOperatorType
    {
        // integer/boolean operations
        Add,
        Subtract,
        Multiply,
        Divide,
        Invert,
        Not,
        And,
        Or,
        ShiftLeft,
        ShiftRight,
        Index,

        // comparisons
        Equal,
        GreaterEqual,
        Greater,
        LessEqual,
        Less,
        NotEqual
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
        [NamedShaderSymbol("uniform", Language = ShaderLanguage.GLSL)]
        Uniform,
        [NamedShaderSymbol("buffer", Language = ShaderLanguage.GLSL)]
        Storage
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
            PushConstant = false;
            Shared = false;
            Flat = false;
        }

        public int Location { get; set; }
        public int Set { get; set; }
        public int Binding { get; set; }
        public ShaderResourceType ResourceType { get; set; }
        public ShaderImageFormat Format { get; set; }
        public bool PushConstant { get; set; }
        public bool Shared { get; set; }
        public bool Flat { get; set; }
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