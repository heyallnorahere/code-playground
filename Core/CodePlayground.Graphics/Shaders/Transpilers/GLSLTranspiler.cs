using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace CodePlayground.Graphics.Shaders.Transpilers
{
    internal sealed class GLSLTranspiler : ShaderTranspiler
    {
        private string TranspileMethod(Type type, MethodInfo method, bool entrypoint)
        {
            var body = method.GetMethodBody();
            var instructions = body?.GetILAsInstructionList(method.Module);

            if (body is null || instructions is null)
            {
                throw new ArgumentException("Cannot transpile a method without a body!");
            }

            var builder = new StringBuilder();
            if (entrypoint)
            {
                builder.AppendLine($"void {method.Name}() {{");
            }
            else
            {
                // todo: parse method signature
            }

            var evaluationStack = new Stack<string>();
            foreach (var instruction in instructions)
            {
                // todo: interpret instruction
            }

            builder.AppendLine("}");
            return builder.ToString();
        }

        protected override string TranspileStage(Type type, MethodInfo entrypoint, ShaderStage stage)
        {
            // todo: process inputs/outputs

            var builder = new StringBuilder();
            builder.AppendLine("#version 450");

            // todo: define inputs/outputs
            // todo: process dependency tree

            string entrypointSource = TranspileMethod(type, entrypoint, true);
            builder.AppendLine(entrypointSource);

            return builder.ToString();
        }
    }
}