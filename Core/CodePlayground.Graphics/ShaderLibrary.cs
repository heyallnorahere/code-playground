using CodePlayground.Graphics.Shaders;
using Optick.NET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CodePlayground.Graphics
{
    public sealed class ShaderLibrary : IDisposable
    {
        public ShaderLibrary(IGraphicsContext context, Assembly assembly)
        {
            mCompiledTypes = new HashSet<Type>();
            mShaders = new Dictionary<string, IShader>();
            mContext = context;
            mDisposed = false;

            Load(assembly);
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

            mDisposed = true;
        }

        public static string GetShaderID<T>() where T : class => GetShaderID(typeof(T));
        public static string GetShaderID(Type type)
        {
            using var getIdEvent = OptickMacros.Event();

            var attribute = type.GetCustomAttribute<CompiledShaderAttribute>();
            if (attribute is null)
            {
                throw new ArgumentException("Type is not a compiled shader!");
            }

            return string.IsNullOrEmpty(attribute.ID) ? type.Name : attribute.ID;
        }

        public static string ConvertResourceName<S, D>(string resourceName) where S : class where D : class => ConvertResourceName(resourceName, typeof(S), typeof(D));
        public static string ConvertResourceName(string resourceName, Type shaderType, Type declaredType)
        {
            if (shaderType != declaredType)
            {
                var declaredName = declaredType.FullName ?? declaredType.Name;
                return $"{declaredName.Replace('.', '_')}_{resourceName}";
            }
            else
            {
                return resourceName;
            }
        }

        private void Load(Assembly assembly)
        {
            using var loadEvent = OptickMacros.Event();

            using var compiler = mContext.CreateCompiler();
            var transpiler = ShaderTranspiler.Create(compiler.PreferredLanguage);

            var assemblyDirectory = Path.GetDirectoryName(assembly.Location);
            var shaderDirectory = Path.Join(assemblyDirectory, "shaders");
            var sourceDirectory = Path.Join(shaderDirectory, "src");
            var binaryDirectory = Path.Join(shaderDirectory, "bin");

            string binaryExtension = compiler.PreferredLanguage switch
            {
                ShaderLanguage.GLSL => "spv",
                ShaderLanguage.HLSL => "bin",
                _ => throw new InvalidOperationException($"Unsupported language: {compiler.PreferredLanguage}")
            };

            var types = assembly.GetTypes();
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

                var source = transpiler.Transpile(type);
                string shaderName = string.IsNullOrEmpty(attribute.ID) ? type.Name : attribute.ID;
                foreach (var stage in source.Keys)
                {
                    string shaderId = Path.Join(shaderName, stage.ToString());
                    if (mShaders.ContainsKey(shaderId))
                    {
                        throw new InvalidOperationException($"Duplicate shader stage: {shaderId}");
                    }

                    var language = transpiler.OutputLanguage;
                    var sourcePath = Path.Join(sourceDirectory, shaderId) + '.' + language.ToString().ToLower();

                    var shaderSourceDirectory = Path.GetDirectoryName(sourcePath);
                    Directory.CreateDirectory(shaderSourceDirectory!);

                    var stageSource = source[stage];
                    using (var sourceStream = new FileStream(sourcePath, FileMode.Create, FileAccess.Write))
                    {
                        using var writer = new StreamWriter(sourceStream, leaveOpen: true);

                        writer.Write(stageSource.Source);
                        writer.Flush();
                    }

                    var binaryPath = Path.Join(binaryDirectory, shaderId) + "." + binaryExtension;
                    var shaderBinaryDirectory = Path.GetDirectoryName(binaryPath);
                    Directory.CreateDirectory(shaderBinaryDirectory!);

                    byte[] bytecode = compiler.Compile(stageSource.Source, sourcePath, language, stage, stageSource.Entrypoint);
                    using (var binaryStream = new FileStream(binaryPath, FileMode.Create, FileAccess.Write))
                    {
                        binaryStream.Write(bytecode);
                        binaryStream.Flush();
                    }

                    var shader = mContext.LoadShader(bytecode, stage, stageSource.Entrypoint);
                    mShaders.Add(shaderId, shader);
                }

                mCompiledTypes.Add(type);
            }
        }

        public IReadOnlyDictionary<ShaderStage, IShader> GetStages(string prefix)
        {
            using var getStagesEvent = OptickMacros.Event();

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

        public IReflectionView CreateReflectionView<T>() where T : class => CreateReflectionView(typeof(T));
        public IReflectionView CreateReflectionView(Type type)
        {
            string id = GetShaderID(type);
            return CreateReflectionView(id);
        }

        public IReflectionView CreateReflectionView(string prefix)
        {
            using var createViewEvent = OptickMacros.Event();

            var stages = GetStages(prefix);
            if (stages.Count == 0)
            {
                throw new ArgumentException($"Failed to find stages matching prefix \"{prefix}\"");
            }

            return mContext.CreateReflectionView(stages);
        }

        public IPipeline LoadPipeline<T>(PipelineDescription description) where T : class => LoadPipeline(typeof(T), description);
        public IPipeline LoadPipeline(Type type, PipelineDescription description)
        {
            string id = GetShaderID(type);
            return LoadPipeline(id, description);
        }

        public IPipeline LoadPipeline(string prefix, PipelineDescription description)
        {
            var pipeline = mContext.CreatePipeline(description);
            LoadPipeline(prefix, pipeline);
            return pipeline;
        }

        public void LoadPipeline<T>(IPipeline pipeline) where T : class => LoadPipeline(typeof(T), pipeline);
        public void LoadPipeline(Type type, IPipeline pipeline)
        {
            string id = GetShaderID(type);
            LoadPipeline(id, pipeline);
        }

        public void LoadPipeline(string prefix, IPipeline pipeline)
        {
            using var loadEvent = OptickMacros.Event();

            var stages = GetStages(prefix);
            if (stages.Count == 0)
            {
                throw new ArgumentException($"Failed to find stages matching prefix \"{prefix}\"");
            }

            pipeline.Load(stages);
        }

        public IGraphicsContext Context => mContext;
        public IReadOnlySet<Type> CompiledTypes => mCompiledTypes;

        private readonly HashSet<Type> mCompiledTypes;
        private readonly Dictionary<string, IShader> mShaders;
        private readonly IGraphicsContext mContext;

        private bool mDisposed;
    }
}