using Silk.NET.Assimp;
using Silk.NET.Maths;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace CodePlayground.Graphics
{
    public struct StaticModelVertex
    {
        public Vector3 Position { get; set; }
        public Vector3 Normal { get; set; }
        public Vector2 UV { get; set; }
    }

    public interface IModelImportContext
    {
        public bool ShouldCopyPostLoad { get; }

        public bool FlipUVs { get; }
        public bool LeftHanded { get; }

        public IDeviceBuffer CreateStaticVertexBuffer(IReadOnlyList<StaticModelVertex> vertices);
        public IDeviceBuffer CreateIndexBuffer(IReadOnlyList<uint> indices);
        public ITexture CreateTexture(ReadOnlySpan<byte> data, int width, int height, DeviceImageFormat format);
        public ITexture LoadTexture(string texturePath, string modelPath, bool loadedFromFile);
        public void CopyBuffers();
    }

    public struct Submesh
    {
        public int VertexOffset { get; set; }
        public int VertexCount { get; set; }
        public int IndexOffset { get; set; }
        public int IndexCount { get; set; }
        public Matrix4x4 Transform { get; set; }
    }

    public abstract class Model : IDisposable
    {
        private static readonly Assimp sAPI;
        static Model()
        {
            sAPI = Assimp.GetApi();
        }

        public static Model? Load(string path, IModelImportContext importContext)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);

            var buffer = new byte[stream.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                totalRead += stream.Read(buffer, totalRead, buffer.Length - totalRead);
            }

            return Load(new ReadOnlySpan<byte>(buffer), path, true, importContext);
        }

        public static Model? Load(ReadOnlySpan<byte> data, string hintPath, IModelImportContext importContext)
        {
            return Load(data, hintPath, false, importContext);
        }

        private static unsafe Model? Load(ReadOnlySpan<byte> data, string path, bool loadedFromFile, IModelImportContext importContext)
        {
            var flags = PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals | PostProcessSteps.GenerateUVCoords | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.LimitBoneWeights;
            if (importContext.FlipUVs)
            {
                flags |= PostProcessSteps.FlipUVs;
            }

            if (importContext.LeftHanded)
            {
                flags |= PostProcessSteps.MakeLeftHanded;
            }

            fixed (byte* ptr = data)
            {
                var scene = sAPI.ImportFileFromMemory(ptr, (uint)data.Length, (uint)flags, string.Empty);
                if (scene is null || scene->MFlags == Assimp.SceneFlagsIncomplete || scene->MRootNode is null)
                {
                    return null;
                }

                bool isStatic = true;
                for (uint i = 0; i < scene->MNumMeshes; i++)
                {
                    var mesh = scene->MMeshes[i];
                    if (mesh->MNumBones > 0)
                    {
                        isStatic = false;
                        break;
                    }
                }

                Model result;
                if (isStatic)
                {
                    result = new StaticModel(scene, path, loadedFromFile, importContext);
                }
                else
                {
                    throw new NotImplementedException("Non-static models are not supported!");
                }

                result.Load();
                return result;
            }
        }

        protected unsafe Model(Scene* scene, string path, bool loadedFromFile, IModelImportContext importContext)
        {
            mDisposed = false;

            mScene = scene;
            mPath = path;
            mLoadedFromFile = loadedFromFile;
            mImportContext = importContext;
            mSubmeshes = new List<Submesh>();
        }

        private unsafe void Load()
        {
            if (!Matrix4x4.Invert(mScene->MRootNode->MTransformation, out Matrix4x4 inverseRootTransform))
            {
                inverseRootTransform = Matrix4x4.Identity;
            }

            ProcessNode(mScene->MRootNode, inverseRootTransform);

            mVertexBuffer = CreateVertexBuffer();
            mIndexBuffer = CreateIndexBuffer();

            if (mImportContext.ShouldCopyPostLoad)
            {
                mImportContext.CopyBuffers();
            }
        }

        ~Model()
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

        protected virtual unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                mVertexBuffer?.Dispose();
                mIndexBuffer?.Dispose();
            }

            sAPI.FreeScene(mScene);
        }

        protected abstract unsafe Submesh ProcessMesh(Node* node, Mesh* mesh, Matrix4x4 transform);
        private unsafe void ProcessNode(Node* node, Matrix4x4 parentTransform)
        {
            var nodeTransform = node->MTransformation;
            var transform = parentTransform * nodeTransform;

            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                uint meshIndex = node->MMeshes[i];
                var mesh = mScene->MMeshes[meshIndex];

                var submesh = ProcessMesh(node, mesh, transform);
                mSubmeshes.Add(submesh);
            }

            for (uint i = 0; i < node->MNumChildren; i++)
            {
                var child = node->MChildren[i];
                ProcessNode(child, transform);
            }
        }

        public IReadOnlyList<Submesh> Submeshes => mSubmeshes;
        public IDeviceBuffer VertexBuffer => mVertexBuffer!;
        public IDeviceBuffer IndexBuffer => mIndexBuffer!;
        public abstract bool IsStatic { get; }

        protected abstract IDeviceBuffer CreateVertexBuffer();
        protected abstract IDeviceBuffer CreateIndexBuffer();

        protected readonly unsafe Scene* mScene;
        private readonly string mPath;
        private readonly bool mLoadedFromFile;
        protected readonly IModelImportContext mImportContext;

        private readonly List<Submesh> mSubmeshes;
        private IDeviceBuffer? mVertexBuffer, mIndexBuffer;

        private bool mDisposed;
    }

    internal sealed class StaticModel : Model
    {
        public unsafe StaticModel(Scene* scene, string path, bool loadedFromFile, IModelImportContext importContext) : base(scene, path, loadedFromFile, importContext)
        {
            mVertices = new List<StaticModelVertex>();
            mIndices = new List<uint>();
        }

        protected override unsafe Submesh ProcessMesh(Node* node, Mesh* mesh, Matrix4x4 transform)
        {
            var submesh = new Submesh
            {
                VertexOffset = mVertices.Count,
                IndexOffset = mIndices.Count,
                Transform = transform
            };

            for (uint i = 0; i < mesh->MNumVertices; i++)
            {
                var position = mesh->MVertices[i];
                var normal = mesh->MNormals[i];
                var uv = mesh->MTextureCoords[0][i];

                mVertices.Add(new StaticModelVertex
                {
                    Position = position,
                    Normal = normal,
                    UV = new Vector2(uv.X, uv.Y)
                });
            }

            for (uint i = 0; i < mesh->MNumFaces; i++)
            {
                var face = mesh->MFaces[i];
                if (face.MNumIndices != 3)
                {
                    throw new ArgumentException("Only triangles are supported!");
                }

                for (uint j = 0; j < face.MNumIndices; j++)
                {
                    mIndices.Add(face.MIndices[j] + (uint)submesh.VertexOffset);
                }
            }

            submesh.VertexCount = mVertices.Count - submesh.VertexOffset;
            submesh.IndexCount = mIndices.Count - submesh.IndexOffset;

            return submesh;
        }

        public override bool IsStatic => true;

        protected override IDeviceBuffer CreateVertexBuffer() => mImportContext.CreateStaticVertexBuffer(mVertices);
        protected override IDeviceBuffer CreateIndexBuffer() => mImportContext.CreateIndexBuffer(mIndices);

        private readonly List<StaticModelVertex> mVertices;
        private readonly List<uint> mIndices;
    }
}
