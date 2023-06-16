using Silk.NET.Assimp;
using Silk.NET.Maths;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

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
        public IGraphicsContext GraphicsContext { get; }

        public bool ShouldCopyPostLoad { get; }
        public bool FlipUVs { get; }
        public bool LeftHanded { get; }

        public IDeviceBuffer CreateStaticVertexBuffer(IReadOnlyList<StaticModelVertex> vertices);
        public IDeviceBuffer CreateIndexBuffer(IReadOnlyList<uint> indices);
        public ITexture CreateTexture(ReadOnlySpan<byte> data, int width, int height, DeviceImageFormat format);
        public ITexture LoadTexture(string texturePath, string modelPath, bool loadedFromFile, ISamplerSettings samplerSettings);
        public void CopyBuffers();
    }

    internal struct ModelSamplerSettings : ISamplerSettings
    {
        public AddressMode AddressMode { get; set; }
        public SamplerFilter Filter { get; set; }
    }

    public struct Submesh
    {
        public int VertexOffset { get; set; }
        public int VertexCount { get; set; }
        public int IndexOffset { get; set; }
        public int IndexCount { get; set; }

        public int MaterialIndex { get; set; }
        public Matrix4x4 Transform { get; set; }
    }

    public abstract class Model : IDisposable
    {
        protected static readonly Assimp sAPI;
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
            mTextures = new Dictionary<string, ITexture>();
            mMaterials = new List<Material>();
        }

        private unsafe void Load()
        {
            if (!Matrix4x4.Invert(mScene->MRootNode->MTransformation, out Matrix4x4 inverseRootTransform))
            {
                inverseRootTransform = Matrix4x4.Identity;
            }

            ProcessNode(mScene->MRootNode, inverseRootTransform);
            for (uint i = 0; i < mScene->MNumMaterials; i++)
            {
                var assimpMaterial = mScene->MMaterials[i];

                var material = ProcessMaterial(assimpMaterial);
                mMaterials.Add(material);
            }

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
                mWhiteTexture?.Dispose();

                foreach (var texture in mTextures.Values)
                {
                    texture.Dispose();
                }

                foreach (var material in mMaterials)
                {
                    material.Dispose();
                }
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

        private unsafe void ProcessMaterialTextures(Silk.NET.Assimp.Material* assimpMaterial, Material material)
        {
            var textureTypes = Enum.GetValues<MaterialTexture>();
            foreach (var textureType in textureTypes)
            {
                var assimpTextureType = textureType switch
                {
                    MaterialTexture.Normal => TextureType.Normals,
                    _ => Enum.Parse<TextureType>(textureType.ToString())
                };

                if (sAPI.GetMaterialTextureCount(assimpMaterial, assimpTextureType) == 0)
                {
                    continue;
                }

                AssimpString path;
                TextureMapMode mapMode;
                if (sAPI.GetMaterialTexture(assimpMaterial, assimpTextureType, 0, &path, null, null, null, null, &mapMode, null) != Return.Success)
                {
                    continue;
                }

                var pathString = Marshal.PtrToStringAnsi((nint)path.Data, (int)path.Length);
                if (!mTextures.TryGetValue(pathString, out ITexture? texture))
                {
                    texture = mImportContext.LoadTexture(pathString, mPath, mLoadedFromFile, new ModelSamplerSettings
                    {
                        AddressMode = mapMode switch
                        {
                            TextureMapMode.Mirror => AddressMode.MirroredRepeat,
                            TextureMapMode.Wrap => AddressMode.Repeat,
                            TextureMapMode.Clamp => AddressMode.ClampToEdge,
                            _ => throw new InvalidOperationException("Unsupported map mode!")
                        },
                        Filter = SamplerFilter.Linear
                    });

                    mTextures.Add(pathString, texture);
                }

                material.Set(textureType, texture);
            }
        }

        private unsafe Material ProcessMaterial(Silk.NET.Assimp.Material* assimpMaterial)
        {
            int blendMode;
            if (sAPI.GetMaterialIntegerArray(assimpMaterial, "$mat.blend", 0, 0, &blendMode, null) != Return.Success)
            {
                blendMode = 0;
            }

            Vector4 tempColor;
            Vector3 diffuseColor, specularColor, ambientColor;

            if (sAPI.GetMaterialColor(assimpMaterial, "$clr.diffuse", 0, 0, &tempColor) == Return.Success)
            {
                diffuseColor = new Vector3(tempColor.X, tempColor.Y, tempColor.Z);
            }
            else
            {
                diffuseColor = new Vector3(1f);
            }

            if (sAPI.GetMaterialColor(assimpMaterial, "$clr.specular", 0, 0, &tempColor) == Return.Success)
            {
                specularColor = new Vector3(tempColor.X, tempColor.Y, tempColor.Z);
            }
            else
            {
                specularColor = new Vector3(1f);
            }

            if (sAPI.GetMaterialColor(assimpMaterial, "$clr.ambient", 0, 0, &tempColor) == Return.Success)
            {
                ambientColor = new Vector3(tempColor.X, tempColor.Y, tempColor.Z);
            }
            else
            {
                ambientColor = new Vector3(1f);
            }

            float shininess, opacity;
            if (sAPI.GetMaterialFloatArray(assimpMaterial, "$mat.shininess", 0, 0, &shininess, null) != Return.Success)
            {
                shininess = 32f;
            }

            if (sAPI.GetMaterialFloatArray(assimpMaterial, "$mat.opacity", 0, 0, &opacity, null) != Return.Success)
            {
                opacity = 1f;
            }

            var data = new byte[] { 255, 255, 255, 255 };
            mWhiteTexture ??= mImportContext.CreateTexture(data, 1, 1, DeviceImageFormat.RGBA8_UNORM);
            var material = new Material(mWhiteTexture, mImportContext.GraphicsContext);

            material.Set("DiffuseColor", diffuseColor);
            material.Set("SpecularColor", specularColor);
            material.Set("AmbientColor", ambientColor);
            material.Set("Shininess", shininess);
            material.Set("Opacity", opacity);

            material.PipelineSpecification.BlendMode = (PipelineBlendMode)(blendMode + 1);
            material.PipelineSpecification.FrontFace = PipelineFrontFace.CounterClockwise;
            material.PipelineSpecification.EnableDepthTesting = true;
            material.PipelineSpecification.DisableCulling = false;

            ProcessMaterialTextures(assimpMaterial, material);
            return material;
        }

        public IReadOnlyList<Submesh> Submeshes => mSubmeshes;
        public IReadOnlyList<Material> Materials => mMaterials;
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
        private readonly Dictionary<string, ITexture> mTextures;
        private readonly List<Material> mMaterials;

        private IDeviceBuffer? mVertexBuffer, mIndexBuffer;
        private ITexture? mWhiteTexture;

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

                MaterialIndex = (int)mesh->MMaterialIndex,
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
