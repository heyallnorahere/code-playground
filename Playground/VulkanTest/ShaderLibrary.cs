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

        private void Load()
        {
            var assemblyDirectory = Path.GetDirectoryName(mAssembly.Location);
            var shaderDirectory = Path.Join(assemblyDirectory, "shaders");
            var sourceDirectory = Path.Join(shaderDirectory, "src");
            var binaryDirectory = Path.Join(shaderDirectory, "bin");

            string binaryExtension = mCompiler.PreferredLanguage switch
            {
                ShaderLanguage.GLSL => "spv",
                ShaderLanguage.HLSL => "bin",
                _ => throw new InvalidOperationException($"Unsupported language: {mCompiler.PreferredLanguage}")
            };

            var types = mAssembly.GetTypes();
            foreach (var type in types)
            {
                if (!type.IsClass)
                {
                    continue;
                }

                var attribute = type.GetCustomAttribute<CompiledShaderAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var source = mTranspiler.Transpile(type);
                string shaderName = string.IsNullOrEmpty(attribute.ID) ? type.Name : attribute.ID;
                foreach (var stage in source.Keys)
                {
                    string shaderId = Path.Join(shaderName, stage.ToString());
                    if (mShaders.ContainsKey(shaderId))
                    {
                        throw new InvalidOperationException($"Duplicate shader stage: {shaderId}");
                    }

                    var language = mTranspiler.OutputLanguage;
                    var sourcePath = Path.Join(sourceDirectory, shaderId) + '.' + language.ToString().ToLower();

                    var shaderSourceDirectory = Path.GetDirectoryName(sourcePath);
                    Directory.CreateDirectory(shaderSourceDirectory!);

                    using var sourceStream = new FileStream(sourcePath, FileMode.Create, FileAccess.Write);
                    using var writer = new StreamWriter(sourceStream);

                    var stageSource = source[stage];
                    writer.Write(stageSource.Source);
                    writer.Flush();

                    byte[] bytecode = mCompiler.Compile(stageSource.Source, sourcePath, language, stage, stageSource.Entrypoint);
                    var shader = mContext.LoadShader(bytecode, stage, stageSource.Entrypoint);
                    mShaders.Add(shaderId, shader);

                    var binaryPath = Path.Join(binaryDirectory, shaderId) + "." + binaryExtension;
                    var shaderBinaryDirectory = Path.GetDirectoryName(binaryPath);
                    Directory.CreateDirectory(shaderBinaryDirectory!);

                    using var binaryStream = new FileStream(binaryPath, FileMode.Create, FileAccess.Write);
                    binaryStream.Write(bytecode);
                    binaryStream.Flush();
                }
            }
        }

        public IReadOnlyDictionary<ShaderStage, IShader> GetStages(string prefix)
        {
            var stages = new Dictionary<ShaderStage, IShader>();
            foreach (var id in mShaders.Keys)
            {
                if (id.StartsWith(prefix))
                {
                    var stageName = Path.GetFileName(id);
                    var stage = Enum.Parse<ShaderStage>(stageName);

                    var shader = mShaders[id];
                    stages.Add(stage, shader);
                }
            }

            return stages;
        }

        public IPipeline LoadPipeline<T>(PipelineDescription description) where T : class => LoadPipeline(typeof(T), description);
        public IPipeline LoadPipeline(Type type, PipelineDescription description)
        {
            var attribute = type.GetCustomAttribute<CompiledShaderAttribute>();
            if (attribute is null)
            {
                throw new ArgumentException("Type is not a compiled shader!");
            }

            string id = string.IsNullOrEmpty(attribute.ID) ? type.Name : attribute.ID;
            return LoadPipeline(id, description);
        }

        public IPipeline LoadPipeline(string prefix, PipelineDescription description)
        {
            var stages = GetStages(prefix);
            if (stages.Count == 0)
            {
                throw new ArgumentException($"Failed to find stages matching prefix \"{prefix}\"");
            }

            var pipeline = mContext.CreatePipeline(description);
            pipeline.Load(stages);
            return pipeline;
        }

        private readonly Assembly mAssembly;
        private readonly IGraphicsContext mContext;
        private readonly IShaderCompiler mCompiler;
        private readonly ShaderTranspiler mTranspiler;
        private readonly Dictionary<string, IShader> mShaders;

        private bool mDisposed;
    }
}
