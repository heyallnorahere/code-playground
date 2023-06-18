using System;

namespace CodePlayground.Graphics.Shaders
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ShaderEntrypointAttribute : Attribute
    {
        public ShaderEntrypointAttribute(ShaderStage stage)
        {
            Stage = stage;
        }

        public ShaderStage Stage { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class PrimitiveShaderTypeAttribute : Attribute
    {
        public PrimitiveShaderTypeAttribute(string name)
        {
            Name = name;
            Instantiable = true;
            IsSampler = false;
        }

        public string Name { get; }
        public bool Instantiable { get; set; }
        public bool IsSampler { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class BuiltinShaderFunctionAttribute : Attribute
    {
        public BuiltinShaderFunctionAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }
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

    [AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Parameter | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class LayoutAttribute : Attribute
    {
        public LayoutAttribute()
        {
            Location = -1;
            Set = Binding = 0;
            ResourceType = ShaderResourceType.Uniform;
            PushConstants = false;
        }

        public int Location { get; set; }
        public int Set { get; set; }
        public int Binding { get; set; }
        public ShaderResourceType ResourceType { get; set; }
        public bool PushConstants { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class ShaderFieldNameAttribute : Attribute
    {
        public ShaderFieldNameAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; set; }
    }

    [AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class OutputPositionAttribute : Attribute
    {
        // nothing - literally just a flag
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class ArraySizeAttribute : Attribute
    {
        public ArraySizeAttribute(int length)
        {
            Length = length;
        }

        public int Length { get; }
    }
}