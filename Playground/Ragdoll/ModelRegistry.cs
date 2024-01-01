using CodePlayground;
using CodePlayground.Graphics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Ragdoll.Layers;
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
        public Vector3 Tangent;
        public int BoneCount;
        public unsafe fixed int BoneIDs[ModelRegistry.BoneLimitPerVertex];
        public unsafe fixed float BoneWeights[ModelRegistry.BoneLimitPerVertex];
    }

    public struct ModelPhysicsData
    {
        public TypedIndex Index { get; set; }
        public Func<float, BodyInertia> ComputeInertia { get; set; }
    }

    public struct LoadedModel
    {
        public Model Model { get; set; }
        public string Name { get; set; }
        public IDeviceBuffer BoneBuffer { get; set; }
        public Dictionary<ulong, int> BoneOffsets { get; set; }
        public Dictionary<ulong, ModelPhysicsData> PhysicsData { get; set; }
    }

    public sealed class ModelRegistry : IModelImportContext, IDisposable
    {
        public const int BoneLimitPerVertex = 4;

        // ImGui drag/drop ID
        public const string RegisteredModelID = "registered-model";

        public ModelRegistry(IGraphicsContext context)
        {
            mCommandList = null;

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
            using var createEvent = Profiler.Event();
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
                    Tangent = src.Tangent,
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

            mCommandList.PushStagingObject(stagingBuffer);
            return buffer;
        }

        IDeviceBuffer IModelImportContext.CreateIndexBuffer(IReadOnlyList<uint> indices)
        {
            using var createEvent = Profiler.Event();
            BeginCommandList();

            var bufferIndices = indices.ToArray();
            int bufferSize = indices.Count * Marshal.SizeOf<uint>();

            var buffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Index, bufferSize);
            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);

            stagingBuffer.CopyFromCPU(bufferIndices);
            stagingBuffer.CopyBuffers(mCommandList, buffer, bufferSize);

            mCommandList.PushStagingObject(stagingBuffer);
            return buffer;
        }

        ITexture IModelImportContext.CreateTexture(ReadOnlySpan<byte> data, int width, int height, DeviceImageFormat format)
        {
            using var createEvent = Profiler.Event();
            var image = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(width, height),
                Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = format
            });

            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, data.Length);
            var newLayout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

            stagingBuffer.CopyFromCPU(data);
            BeginCommandList();

            image.TransitionLayout(mCommandList, image.Layout, newLayout);
            image.CopyFromBuffer(mCommandList, stagingBuffer, newLayout);
            image.Layout = newLayout;

            mCommandList.PushStagingObject(stagingBuffer);
            return image.CreateTexture(true);
        }

        ITexture IModelImportContext.LoadTexture(string texturePath, string modelPath, bool loadedFromFile, ISamplerSettings samplerSettings)
        {
            using var loadEvent = Profiler.Event();
            if (!loadedFromFile)
            {
                throw new NotImplementedException();
            }

            var path = Path.IsPathFullyQualified(texturePath) ? texturePath : Path.GetFullPath(Path.Join(Path.GetDirectoryName(modelPath), texturePath.Replace('\\', '/')));
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
            var newLayout = deviceImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

            stagingBuffer.CopyFromCPU(pixelData);
            BeginCommandList();

            deviceImage.TransitionLayout(mCommandList, deviceImage.Layout, newLayout);
            deviceImage.CopyFromBuffer(mCommandList, stagingBuffer, newLayout);
            deviceImage.Layout = newLayout;

            mCommandList.PushStagingObject(stagingBuffer);
            return deviceImage.CreateTexture(true);
        }

        void IModelImportContext.CopyBuffers()
        {
            using var copyBuffersEvent = Profiler.Event();
            if (mCommandList is null)
            {
                return;
            }

            mCommandList.End();

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            queue.Submit(mCommandList, true);

            mCommandList = null;
        }

        #endregion
        #region Model registry

        public IReadOnlyDictionary<int, LoadedModel> Models => mModels;

        public int Load<T>(string path, string name, string bufferName) where T : class => Load(path, name, typeof(T), bufferName);
        public int Load(string path, string name, Type shader, string bufferName)
        {
            using var loadEvent = Profiler.Event();

            var model = Model.Load(path, this);
            if (model is null)
            {
                return -1;
            }

            var renderer = App.Instance.Renderer!;
            var reflectionView = renderer.Library.CreateReflectionView(shader);

            var skeleton = model.Skeleton;
            int bufferSize = (skeleton?.BoneCount ?? 1) * Marshal.SizeOf<Matrix4x4>();

            int id = mCurrentModelID++;
            var modelData = new LoadedModel
            {
                Model = model,
                Name = name,
                BoneBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, bufferSize),
                BoneOffsets = new Dictionary<ulong, int>(),
                PhysicsData = new Dictionary<ulong, ModelPhysicsData>()
            };

            mModels.Add(id, modelData);
            return id;
        }

        public void SetEntityColliderScale(int model, Scene scene, ulong entity, Vector3 scale)
        {
            using var setColliderScaleEvent = Profiler.Event();

            var modelData = mModels[model];
            if (modelData.PhysicsData.TryGetValue(entity, out ModelPhysicsData physicsData))
            {
                var simulation = scene.Simulation;
                simulation.Shapes.RecursivelyRemoveAndDispose(physicsData.Index, simulation.BufferPool);
            }

            CreateCompoundShape(modelData.Model, scene, scale, out physicsData);
            modelData.PhysicsData[entity] = physicsData;
        }

        public void RemoveEntityCollider(int model, Scene scene, ulong entity)
        {
            using var removeColliderEvent = Profiler.Event();

            var modelData = mModels[model];
            if (!modelData.PhysicsData.TryGetValue(entity, out ModelPhysicsData physicsData))
            {
                return;
            }

            var simulation = scene.Simulation;
            simulation.Shapes.RecursivelyRemoveAndDispose(physicsData.Index, simulation.BufferPool);

            modelData.PhysicsData.Remove(entity);
        }

        private static void CreateCompoundShape(Model model, Scene scene, Vector3 scale, out ModelPhysicsData physicsData)
        {
            using var createShapeEvent = Profiler.Event();
            model.GetMeshData(out Vector3[] vertices, out int[] indices);

            var simulation = scene.Simulation;
            var bufferPool = simulation.BufferPool;

            var triangles = new List<Triangle>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                triangles.Add(new Triangle
                {
                    A = vertices[indices[i]],
                    B = vertices[indices[i + 1]],
                    C = vertices[indices[i + 2]]
                });
            }

            bufferPool.Take(triangles.Count, out Buffer<Triangle> buffer);
            for (int i = 0; i < triangles.Count; i++)
            {
                buffer[i] = triangles[i];
            }

            var mesh = new Mesh(buffer, scale, bufferPool);
            physicsData = new ModelPhysicsData
            {
                Index = simulation.Shapes.Add(mesh),
                ComputeInertia = mesh.ComputeClosedInertia
            };
        }

        public int CreateBoneOffset(int model, ulong entity)
        {
            using var createOffsetEvent = Profiler.Event();

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
            using var clearEvent = Profiler.Event();

            var appLayers = App.Instance.LayerView;
            var scene = appLayers.FindLayer<SceneLayer>()?.Scene;

            foreach (var model in mModels.Values)
            {
                model.Model.Dispose();
                model.BoneBuffer.Dispose();

                if (scene is not null)
                {
                    var simulation = scene.Simulation;
                    foreach (var physicsData in model.PhysicsData.Values)
                    {
                        simulation.Shapes.RecursivelyRemoveAndDispose(physicsData.Index, simulation.BufferPool);
                    }
                }
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

        private int mCurrentModelID;
        private readonly Dictionary<int, LoadedModel> mModels;

        private readonly IGraphicsContext mContext;
        private bool mDisposed;
    }
}
