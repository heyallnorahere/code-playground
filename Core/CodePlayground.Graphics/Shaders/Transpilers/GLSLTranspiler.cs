using System;
using System.Reflection;

namespace CodePlayground.Graphics.Shaders.Transpilers
{
    internal sealed class GLSLTranspiler : ShaderTranspiler
    {
        protected override string TranspileStage(Type type, MethodInfo entrypoint, ShaderStage stage)
        {
            var definition = FindMethodDefinition(entrypoint);
            var instructions = definition.Body.Instructions;

            foreach (var instruction in instructions)
            {
                // todo: parse, recursively convert to glsl
            }

            return string.Empty;
        }
    }
}