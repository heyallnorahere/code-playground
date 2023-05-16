using CodePlayground.Graphics.Shaders.Transpilers;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
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

        public static ShaderTranspiler Create(ShaderLanguage language, AssemblyDefinition definition)
        {
            var transpiler = language switch
            {
                ShaderLanguage.GLSL => new GLSLTranspiler(),
                _ => throw new ArgumentException("Unsupported shader language!")
            };

            transpiler.mAssembly = definition;
            transpiler.OutputLanguage = language;

            return transpiler;
        }

        protected MethodDefinition FindMethodDefinition(MethodInfo method)
        {
            var module = mAssembly!.Modules[0];
            var reflectionType = method.DeclaringType!;

            var reference = module.GetType(reflectionType.FullName!, true);
            var cecilType = reference.Resolve();

            var parameters = method.GetParameters();
            foreach (var cecilMethod in cecilType.Methods)
            {
                if (cecilMethod.Name != method.Name)
                {
                    continue;
                }

                if (parameters.Length != cecilMethod.Parameters.Count)
                {
                    continue;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    reflectionType = parameters[i].ParameterType;
                    reference = cecilMethod.Parameters[i].ParameterType;
                    
                    if (reference.FullName != reflectionType.FullName)
                    {
                        continue;
                    }
                }

                return cecilMethod;
            }

            throw new ArgumentException("Failed to find method!");
        }

        protected static Type? ResolveType(TypeReference reference)
        {
            string assemblyName = reference.Module.Assembly.Name.Name;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            Assembly? typeAssembly = null;
            foreach (var assembly in assemblies)
            {
                if (assembly.GetName().Name == assemblyName)
                {
                    typeAssembly = assembly;
                }
            }

            if (typeAssembly is null)
            {
                throw new ArgumentException("Couldn't find the specified assembly!");
            }

            return typeAssembly.GetType(reference.FullName);
        }

        protected abstract string TranspileStage(Type type, MethodInfo entrypoint, ShaderStage stage);
        public IReadOnlyDictionary<ShaderStage, StageOutput> Transpile(Type type)
        {
            if (!type.Extends(typeof(ShaderBase)))
            {
                throw new ArgumentException("Shader must extend ShaderBase!");
            }

            var stages = new Dictionary<ShaderStage, StageOutput>();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            
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

                stages.Add(stage, new StageOutput
                {
                    Source = TranspileStage(type, method, stage),
                    Entrypoint = method.Name
                });
            }

            return stages;
        }

        public IReadOnlyDictionary<ShaderStage, StageOutput> Transpile<T>() where T : ShaderBase
        {
            return Transpile(typeof(T));
        }

        public ShaderLanguage OutputLanguage { get; private set; }

        private AssemblyDefinition? mAssembly;
    }
}