using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics
{
    public enum MaterialTexture
    {
        Diffuse,
        Specular,
        Ambient,
        Normal
    }

    public sealed class MaterialPipelineSpecification : IPipelineSpecification
    {
        internal MaterialPipelineSpecification()
        {
            BlendMode = PipelineBlendMode.None;
            FrontFace = PipelineFrontFace.Clockwise;
            EnableDepthTesting = true;
            DisableCulling = false;
        }

        public PipelineBlendMode BlendMode { get; set; }
        public PipelineFrontFace FrontFace { get; set; }
        public bool EnableDepthTesting { get; set; }
        public bool DisableCulling { get; set; }
    }

    public sealed class Material : IDisposable
    {
        public static IPipelineSpecification CreateDefaultPipelineSpec() => new MaterialPipelineSpecification();
        public Material(string name, ITexture whiteTexture, IGraphicsContext context)
        {
            PipelineSpecification = new MaterialPipelineSpecification();

            mUniformBuffers = new Dictionary<ulong, IDeviceBuffer>();
            mTextures = new Dictionary<MaterialTexture, ITexture>();
            mFields = new Dictionary<string, byte[]>();

            mName = name;
            mWhiteTexture = whiteTexture;
            mContext = context;

            mDisposed = false;
        }

        ~Material()
        {
            if (!mDisposed)
            {
                Dispose(false);
                mDisposed = true;
            }
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Dispose(true);
            mDisposed = true;
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var buffer in mUniformBuffers.Values)
                {
                    buffer.Dispose();
                }
            }
        }

        public unsafe void Set<T>(string name, T data) where T : unmanaged
        {
            var buffer = new byte[sizeof(T)];
            MemoryMarshal.Write(buffer, ref data);
            mFields[name] = buffer;
        }

        public void Set(MaterialTexture type, ITexture texture)
        {
            mTextures[type] = texture;
        }

        public void Bind(IPipeline pipeline, string bufferName, Func<MaterialTexture, string> textureNameCallback)
        {
            var reflectionView = pipeline.ReflectionView;
            if (!reflectionView.ResourceExists(bufferName))
            {
                throw new ArgumentException($"Buffer \"{bufferName}\" does not exist!");
            }

            if (!mUniformBuffers.TryGetValue(pipeline.ID, out IDeviceBuffer? uniformBuffer))
            {
                int bufferSize = reflectionView.GetBufferSize(bufferName);
                uniformBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, bufferSize);
                mUniformBuffers.Add(pipeline.ID, uniformBuffer);
            }

            pipeline.Bind(uniformBuffer, bufferName, 0);
            uniformBuffer.Map(mapped =>
            {
                var textureFlags = new Dictionary<string, bool>();
                var textureTypes = Enum.GetValues<MaterialTexture>();

                foreach (var textureType in textureTypes)
                {
                    textureFlags[$"Has{textureType}Map"] = mTextures.ContainsKey(textureType);
                }

                var keyList = new HashSet<string>(mFields.Keys.Concat(textureFlags.Keys));
                foreach (var fieldName in keyList)
                {
                    int gpuOffset = reflectionView.GetBufferOffset(bufferName, fieldName);
                    if (gpuOffset < 0)
                    {
                        continue;
                    }

                    if (mFields.TryGetValue(fieldName, out byte[]? fieldData))
                    {
                        for (int i = 0; i < fieldData.Length; i++)
                        {
                            mapped[gpuOffset + i] = fieldData[i];
                        }
                    }
                    else
                    {
                        mapped[gpuOffset] = (byte)(textureFlags[fieldName] ? 1 : 0); // im sorry
                    }
                }
            });

            var textureTypes = Enum.GetValues<MaterialTexture>();
            foreach (var textureType in textureTypes)
            {
                string textureName = textureNameCallback.Invoke(textureType);
                if (!reflectionView.ResourceExists(textureName))
                {
                    continue;
                }

                var texture = mTextures.GetValueOrDefault(textureType) ?? mWhiteTexture;
                pipeline.Bind(texture, textureName, 0);
            }
        }

        public string Name => mName;
        public MaterialPipelineSpecification PipelineSpecification { get; }

        private readonly Dictionary<ulong, IDeviceBuffer> mUniformBuffers;
        private readonly Dictionary<MaterialTexture, ITexture> mTextures;
        private readonly Dictionary<string, byte[]> mFields;

        private readonly string mName;
        private readonly ITexture mWhiteTexture;
        private readonly IGraphicsContext mContext;

        private bool mDisposed;
    }
}
