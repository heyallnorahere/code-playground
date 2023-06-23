using CodePlayground.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ragdoll
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public int BoneCount;
        public unsafe fixed int BoneIDs[ModelRegistry.BoneLimitPerVertex];
        public unsafe fixed float BoneWeights[ModelRegistry.BoneLimitPerVertex];
    }

    public struct LoadedModel
    {
        public Model Model { get; set; }
        public string Name { get; set; }
        public IDeviceBuffer BoneBuffer { get; set; }
        public Dictionary<ulong, int> BoneOffsets { get; set; }
    }

    public sealed class ModelRegistry : IModelImportContext, IDisposable
    {
        public const int BoneLimitPerVertex = 4;

        public ModelRegistry(IGraphicsContext context)
        {
            mCommandList = null;
            mStagingBuffers = new List<IDeviceBuffer>();

            mCurrentModelID = 0;
            mModels = new Dictionary<int, LoadedModel>();

            mContext = context;
            mDisposed = false;
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Clear();
            mDisposed = true;
        }

        #region IModelImportContext implementation

        IGraphicsContext IModelImportContext.GraphicsContext => mContext;

        bool IModelImportContext.ShouldCopyPostLoad => true;
        int IModelImportContext.MaxBonesPerVertex => BoneLimitPerVertex;

        [MemberNotNull(nameof(mCommandList))]
        private void BeginCommandList()
        {
            if (mCommandList is not null)
            {
                return;
            }

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            mCommandList = queue.Release();
            mCommandList.Begin();
        }

        unsafe IDeviceBuffer IModelImportContext.CreateVertexBuffer(IReadOnlyList<ModelVertex> vertices)
        {
            BeginCommandList();

            var bufferVertices = new Vertex[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                var src = vertices[i];
                var dst = new Vertex
                {
                    Position = src.Position,
                    Normal = src.Normal,
                    UV = src.UV,
                    BoneCount = src.Bones.Count
                };

                for (int j = 0; j < src.Bones.Count; j++)
                {
                    var bone = src.Bones[j];
                    dst.BoneIDs[j] = bone.Index;
                    dst.BoneWeights[j] = bone.Weight;
                }

                bufferVertices[i] = dst;
            }

            int bufferSize = vertices.Count * Marshal.SizeOf<Vertex>();
            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);
            var buffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Vertex, bufferSize);

            stagingBuffer.CopyFromCPU(bufferVertices);
            stagingBuffer.CopyBuffers(mCommandList, buffer, bufferSize);

            mStagingBuffers.Add(stagingBuffer);
            return buffer;
        }

        IDeviceBuffer IModelImportContext.CreateIndexBuffer(IReadOnlyList<uint> indices)
        {
            BeginCommandList();

            var bufferIndices = indices.ToArray();
            int bufferSize = indices.Count * Marshal.SizeOf<uint>();

            var buffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Index, bufferSize);
            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);

            stagingBuffer.CopyFromCPU(bufferIndices);
            stagingBuffer.CopyBuffers(mCommandList, buffer, bufferSize);

            mStagingBuffers.Add(stagingBuffer);
            return buffer;
        }

        ITexture IModelImportContext.CreateTexture(ReadOnlySpan<byte> data, int width, int height, DeviceImageFormat format)
        {
            BeginCommandList();

            var image = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(width, height),
                Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = format
            });

            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, data.Length);
            stagingBuffer.CopyFromCPU(data);

            var newLayout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            image.TransitionLayout(mCommandList, image.Layout, newLayout);
            image.CopyFromBuffer(mCommandList, stagingBuffer, newLayout);
            image.Layout = newLayout;

            mStagingBuffers.Add(stagingBuffer);
            return image.CreateTexture(true);
        }

        ITexture IModelImportContext.LoadTexture(string texturePath, string modelPath, bool loadedFromFile, ISamplerSettings samplerSettings)
        {
            if (!loadedFromFile)
            {
                throw new NotImplementedException();
            }

            var path = Path.IsPathFullyQualified(texturePath) ? texturePath : Path.GetFullPath(Path.Join(Path.GetDirectoryName(modelPath), texturePath));
            var image = Image.Load<Rgba32>(path);

            var deviceImage = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = image.Size,
                Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = DeviceImageFormat.RGBA8_UNORM
            });

            var pixelData = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(pixelData);

            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, pixelData.Length * Marshal.SizeOf<Rgba32>());
            stagingBuffer.CopyFromCPU(pixelData);

            var newLayout = deviceImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            BeginCommandList();
            deviceImage.TransitionLayout(mCommandList, deviceImage.Layout, newLayout);
            deviceImage.CopyFromBuffer(mCommandList, stagingBuffer, newLayout);
            deviceImage.Layout = newLayout;

            mStagingBuffers.Add(stagingBuffer);
            return deviceImage.CreateTexture(true);
        }

        void IModelImportContext.CopyBuffers()
        {
            if (mCommandList is null)
            {
                return;
            }

            mCommandList.End();

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            queue.Submit(mCommandList, true);

            mStagingBuffers.ForEach(buffer => buffer.Dispose());
            mStagingBuffers.Clear();

            mCommandList = null;
        }

        #endregion
        #region Model registry

        public IReadOnlyDictionary<int, LoadedModel> Models => mModels;
        
        public int Load<T>(string path, string name, string bufferName) where T : class => Load(path, name, typeof(T), bufferName);
        public int Load(string path, string name, Type shader, string bufferName)
        {
            var model = Model.Load(path, this);
            if (model is null)
            {
                return -1;
            }

            var renderer = App.Instance.Renderer!;
            var reflectionView = renderer.Library.CreateReflectionView(shader);
            
            int bufferSize = reflectionView.GetBufferSize(bufferName);
            if (bufferSize < 0)
            {
                throw new ArgumentException($"Failed to find buffer {bufferName} in shader {ShaderLibrary.GetShaderID(shader)}!");
            }

            int id = mCurrentModelID++;
            mModels.Add(id, new LoadedModel
            {
                Model = model,
                Name = name,
                BoneBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, bufferSize),
                BoneOffsets = new Dictionary<ulong, int>()
            });

            return id;
        }

        public int CreateBoneOffset(int model, ulong entity)
        {
            var data = mModels[model];
            int boneCount = data.Model.Skeleton?.BoneCount ?? 0;
            if (boneCount == 0)
            {
                return 0;
            }

            if (data.BoneOffsets.TryGetValue(entity, out int offset))
            {
                return offset;
            }

            offset = 0;
            while (data.BoneOffsets.ContainsValue(offset))
            {
                offset += boneCount;
            }

            return offset;
        }

        public void Clear()
        {
            foreach (var model in mModels.Values)
            {
                model.Model.Dispose();
            }

            mModels.Clear();
        }

        public string GetFormattedName(int id)
        {
            if (id < 0)
            {
                return "--No model--";
            }

            return $"{mModels[id].Name} (ID: {id})";
        }

        #endregion

        private ICommandList? mCommandList;
        private readonly List<IDeviceBuffer> mStagingBuffers;

        private int mCurrentModelID;
        private readonly Dictionary<int, LoadedModel> mModels;

        private readonly IGraphicsContext mContext;
        private bool mDisposed;
    }
}
