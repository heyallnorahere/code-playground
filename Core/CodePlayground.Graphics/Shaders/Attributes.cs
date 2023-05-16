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
            IsGeneric = false;
        }

        public string Name { get; }
        public bool IsGeneric { get; set; }
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

    [AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Parameter | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class ShaderLocationAttribute : Attribute
    {
        public ShaderLocationAttribute(uint location)
        {
            Location = location;
        }

        public uint Location { get; }
    }
}