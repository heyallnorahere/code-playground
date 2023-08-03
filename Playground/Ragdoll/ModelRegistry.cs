﻿using CodePlayground.Graphics;
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
        public int BoneCount;
        public unsafe fixed int BoneIDs[ModelRegistry.BoneLimitPerVertex];
        public unsafe fixed float BoneWeights[ModelRegistry.BoneLimitPerVertex];
    }

    public struct ModelPhysicsData
    {
        public TypedIndex Shape { get; set; }
        public BodyInertia Inertia { get; set; }
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
            var modelData = mModels[model];
            if (modelData.PhysicsData.TryGetValue(entity, out ModelPhysicsData physicsData))
            {
                var simulation = scene.Simulation;
                simulation.Shapes.RecursivelyRemoveAndDispose(physicsData.Shape, simulation.BufferPool);
            }

            CreateCompoundShape(modelData.Model, scene, scale, out physicsData);
            modelData.PhysicsData[entity] = physicsData;
        }

        public void RemoveEntityCollider(int model, Scene scene, ulong entity)
        {
            var modelData = mModels[model];
            if (!modelData.PhysicsData.TryGetValue(entity, out ModelPhysicsData physicsData))
            {
                return;
            }

            var simulation = scene.Simulation;
            simulation.Shapes.RecursivelyRemoveAndDispose(physicsData.Shape, simulation.BufferPool);

            modelData.PhysicsData.Remove(entity);
        }

        private static void CreateCompoundShape(Model model, Scene scene, Vector3 scale, out ModelPhysicsData physicsData)
        {
            model.GetMeshData(out Vector3[] vertices, out int[] indices);

            var simulation = scene.Simulation;
            using var builder = new CompoundBuilder(simulation.BufferPool, simulation.Shapes, 2);

            for (int i = 0; i < indices.Length; i += 3)
            {
                var triangle = new Triangle
                {
                    A = vertices[indices[i]] * scale,
                    B = vertices[indices[i + 1]] * scale,
                    C = vertices[indices[i + 2]] * scale
                };

                float a = (triangle.A - triangle.B).Length();
                float b = (triangle.B - triangle.C).Length();
                float c = (triangle.C - triangle.A).Length();

                // c2 = a2 + b2 - 2ab * cos(theta)
                // a2 + b2 - c2 = 2ab * cos(theta)
                // (a2 + b2 - c2) / 2ab = cos(theta)
                // theta = acos((a2 + b2 - c2) / 2ab)
                // theta is the angle between line a and line b (angle ABC)
                float cosTheta = (MathF.Pow(a, 2f) + MathF.Pow(b, 2f) - MathF.Pow(c, 2f)) / (2f * a * b);

                // b2 = (cos(theta) * b) ^ 2 + h2
                // h2 = b2 - (cos(theta) * b) ^ 2
                // h2 = b2(1 - cos(theta) ^ 2)
                // h = sqrt(b2(1 - cos(theta) ^ 2))
                float height = MathF.Sqrt(MathF.Pow(b, 2f) * (1f - MathF.Pow(cosTheta, 2f)));

                // just using surface area as the weight
                // todo: change
                float area = a * height / 2f;

                builder.Add(triangle, RigidPose.Identity, area);
            }

            builder.BuildDynamicCompound(out Buffer<CompoundChild> children, out BodyInertia inertia);
            physicsData = new ModelPhysicsData
            {
                Shape = simulation.Shapes.Add(new Compound(children)),
                Inertia = inertia
            };
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
                        simulation.Shapes.RecursivelyRemoveAndDispose(physicsData.Shape, simulation.BufferPool);
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
        private readonly List<IDeviceBuffer> mStagingBuffers;

        private int mCurrentModelID;
        private readonly Dictionary<int, LoadedModel> mModels;

        private readonly IGraphicsContext mContext;
        private bool mDisposed;
    }
}
