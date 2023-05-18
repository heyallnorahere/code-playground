using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VulkanTest
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

    public sealed class ShaderLibrary : IDisposable
    {
        public ShaderLibrary(GraphicsApplication application)
        {
            mAssembly = application.GetType().Assembly;
            mContext = application.GraphicsContext!;
            mCompiler = mContext.CreateCompiler();
            mTranspiler = ShaderTranspiler.Create(mCompiler.PreferredLanguage);
            mShaders = new Dictionary<string, IShader>();
            mDisposed = false;

            Load();
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            foreach (var shader in mShaders.Values)
            {
                shader.Dispose();
            }

            mCompiler.Dispose();
            mDisposed = true;
        }

        private TextReader? GetResourceStream(string name)
        {
            var stream = mAssembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            return new StreamReader(stream, leaveOpen: false);
        }

        private void Load()
        {
            var types = mAssembly.GetTypes();
            foreach (var type in types)
            {
                var attribute = type.GetCustomAttribute<CompiledShaderAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var source = mTranspiler.Transpile(type);
                string shaderName = string.IsNullOrEmpty(attribute.ID) ? type.Name : attribute.ID;
                foreach (var stage in source.Keys)
                {
                    string shaderId = $"{shaderName}/{stage}";
                    if (mShaders.ContainsKey(shaderId))
                    {
                        throw new InvalidOperationException($"Duplicate shader stage: {shaderId}");
                    }

                    var stageSource = source[stage];
                    string path = $"{type.FullName}::{stageSource.Entrypoint}";

                    byte[] bytecode = mCompiler.Compile(stageSource.Source, path, mTranspiler.OutputLanguage, stage, stageSource.Entrypoint);
                    var shader = mContext.LoadShader(bytecode, stage, stageSource.Entrypoint);

                    mShaders.Add(shaderId, shader);
                }
            }
        }

        public IReadOnlyDictionary<string, IShader> Shaders => mShaders;

        private readonly Assembly mAssembly;
        private readonly IGraphicsContext mContext;
        private readonly IShaderCompiler mCompiler;
        private readonly ShaderTranspiler mTranspiler;
        private readonly Dictionary<string, IShader> mShaders;

        private bool mDisposed;
    }
}
