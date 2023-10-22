using CodePlayground.Graphics.Shaders.Transpilers;
using Optick.NET;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace CodePlayground.Graphics.Shaders
{
    public abstract class ShaderTranspiler
    {
        public struct StageOutput
        {
            public string Source { get; set; }
            public string Entrypoint { get; set; }
        }

        public static ShaderTranspiler Create(ShaderLanguage language)
        {
            var transpiler = language switch
            {
                ShaderLanguage.GLSL => new GLSLTranspiler(),
                _ => throw new ArgumentException("Unsupported shader language!")
            };

            transpiler.OutputLanguage = language;
            return transpiler;
        }

        protected abstract StageOutput TranspileStage(Type type, MethodInfo entrypoint, ShaderStage stage);
        public IReadOnlyDictionary<ShaderStage, StageOutput> Transpile([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type)
        {
            using var transpileEvent = OptickMacros.Event();

            if (!type.IsClass)
            {
                throw new ArgumentException("Shader type must be a class!");
            }

            var stages = new Dictionary<ShaderStage, StageOutput>();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            
            foreach (var method in methods)
            {
                var entrypointAttribute = method.GetCustomAttribute<ShaderEntrypointAttribute>();
                if (entrypointAttribute is null)
                {
                    continue;
                }

                var stage = entrypointAttribute.Stage;
                if (stages.ContainsKey(stage))
                {
                    throw new InvalidOperationException("Duplicate shader stage!");
                }

                stages.Add(stage, TranspileStage(type, method, stage));
            }

            return stages;
        }

        public IReadOnlyDictionary<ShaderStage, StageOutput> Transpile<T>() where T : class
        {
            return Transpile(typeof(T));
        }

        public ShaderLanguage OutputLanguage { get; private set; }
    }
}