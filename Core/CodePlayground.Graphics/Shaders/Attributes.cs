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
}