using CodePlayground.Graphics;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VulkanTest
{
    [JsonObject(ItemRequired = Required.Always)]
    internal struct ShaderMetadata
    {
        public ShaderLanguage Language { get; set; }
        public ShaderType Type { get; set; }
        [JsonProperty(Required = Required.Default)]
        public string? Entrypoint { get; set; }
    }

    public sealed class ShaderLibrary : IDisposable
    {
        public ShaderLibrary(IGraphicsContext context, Assembly assembly)
        {
            mAssembly = assembly;
            mContext = context;
            mCompiler = mContext.CreateCompiler();
            mShaders = new Dictionary<string, IShader>();
            mDisposed = false;

            mSerializer = JsonSerializer.CreateDefault();
            mSerializer.Converters.Add(new StringEnumConverter());

            Reload();
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            CleanupShaders();
            mCompiler.Dispose();

            mDisposed = true;
        }

        private void CleanupShaders()
        {
            foreach (var shader in mShaders.Values)
            {
                shader.Dispose();
            }

            mShaders.Clear();
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

        public void Reload()
        {
            CleanupShaders();

            string dataRoot = $"{mAssembly.GetName().Name}.Shaders";
            string metadataFile = $"{dataRoot}.Metadata.json";

            using var metadataFileReader = GetResourceStream(metadataFile);
            if (metadataFileReader is null)
            {
                throw new FileNotFoundException("Failed to find metadata file!");
            }

            using var jsonReader = new JsonTextReader(metadataFileReader)
            {
                CloseInput = false
            };

            var metadataDirectory = mSerializer.Deserialize<Dictionary<string, ShaderMetadata>>(jsonReader);
            if (metadataDirectory is null)
            {
                throw new InvalidOperationException("Failed to parse metadata JSON!");
            }

            foreach (string relativePath in metadataDirectory.Keys)
            {
                if (relativePath.Contains(".."))
                {
                    throw new ArgumentException("Cannot escape shader directory!");
                }

                var relativeUnixPath = relativePath.Replace('\\', '/');
                var relativeName = relativeUnixPath.Replace('/', '.');
                var resourceName = $"{dataRoot}.{relativeName}";

                var info = mAssembly.GetManifestResourceInfo(resourceName);
                if (info is null)
                {
                    throw new InvalidOperationException($"Invalid resource: {resourceName}");
                }

                var segments = relativeUnixPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (info.FileName is not null && info.FileName != segments[^1])
                {
                    throw new InvalidOperationException($"No such file: {relativeUnixPath}");
                }

                using var reader = GetResourceStream(resourceName);
                if (reader is null)
                {
                    throw new InvalidOperationException("Failed to open stream to shader file!");
                }

                var source = reader.ReadToEnd();
                var metadata = metadataDirectory[relativePath];
                string entrypoint = metadata.Entrypoint ?? "main";

                byte[] bytecode = mCompiler.Compile(source, $"<manifest>:{relativeUnixPath}", metadata.Language, metadata.Type, entrypoint);
                var shader = mContext.LoadShader(bytecode, metadata.Type, entrypoint);

                mShaders.Add(relativeUnixPath, shader);
            }
        }

        public IReadOnlyDictionary<string, IShader> Shaders => mShaders;

        private readonly Assembly mAssembly;
        private readonly JsonSerializer mSerializer;
        private readonly IGraphicsContext mContext;
        private readonly IShaderCompiler mCompiler;
        private readonly Dictionary<string, IShader> mShaders;

        private bool mDisposed;
    }
}
