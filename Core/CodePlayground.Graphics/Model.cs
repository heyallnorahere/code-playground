using Optick.NET;
using Silk.NET.Assimp;
using Silk.NET.SDL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Skeleton = CodePlayground.Graphics.Animation.Skeleton;

namespace CodePlayground.Graphics
{
    public struct BoneData
    {
        public int Index { get; set; }
        public float Weight { get; set; }
    }

    internal struct ProcessedBone
    {
        public int InitialIndex { get; set; }
        public int Parent { get; set; }
        public string Name { get; set; }
        public unsafe Node* Node { get; set; }
        public unsafe Bone* Bone { get; set; }
    }

    public struct ModelVertex
    {
        public Vector3 Position { get; set; }
        public Vector3 Normal { get; set; }
        public Vector2 UV { get; set; }
        public List<BoneData> Bones { get; set; }
    }

    public interface IModelImportContext
    {
        public IGraphicsContext GraphicsContext { get; }

        public bool ShouldCopyPostLoad { get; }
        public int MaxBonesPerVertex { get; }

        public IDeviceBuffer CreateVertexBuffer(IReadOnlyList<ModelVertex> vertices);
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
        public bool HasBones { get; set; }
    }

    public sealed class Model : IDisposable
    {
        private static readonly Assimp sAPI;
        static Model()
        {
            sAPI = Assimp.GetApi();
        }

        private static PostProcessSteps GetImportFlags(IModelImportContext importContext)
        {
            var flags = PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals | PostProcessSteps.GenerateUVCoords | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.LimitBoneWeights;

            var graphicsContext = importContext.GraphicsContext;
            if (graphicsContext.FlipUVs)
            {
                flags |= PostProcessSteps.FlipUVs;
            }

            if (graphicsContext.LeftHanded)
            {
                flags |= PostProcessSteps.MakeLeftHanded;
            }

            return flags;
        }

        public static unsafe Model? Load(string path, IModelImportContext importContext)
        {
            using var loadEvent = OptickMacros.Event();

            var scene = sAPI.ImportFile(path, (uint)GetImportFlags(importContext));
            return Load(scene, path, true, importContext);
        }

        public static unsafe Model? Load(ReadOnlySpan<byte> data, string hintPath, IModelImportContext importContext)
        {
            using var loadEvent = OptickMacros.Event();

            fixed (byte* ptr = data)
            {
                var scene = sAPI.ImportFileFromMemory(ptr, (uint)data.Length, (uint)GetImportFlags(importContext), string.Empty);
                return Load(scene, hintPath, false, importContext);
            }
        }

        private static unsafe Model? Load(Scene* scene, string path, bool loadedFromFile, IModelImportContext importContext)
        {
            using var loadEvent = OptickMacros.Event();
            if (scene is null || (scene->MFlags & Assimp.SceneFlagsIncomplete) != 0 || scene->MRootNode is null)
            {
                return null;
            }

            return new Model(scene, path, loadedFromFile, importContext);
        }

        private unsafe Model(Scene* scene, string path, bool loadedFromFile, IModelImportContext importContext)
        {
            using var constructorEvent = OptickMacros.Event();
            mDisposed = false;

            mScene = scene;
            mPath = path;
            mLoadedFromFile = loadedFromFile;
            mImportContext = importContext;

            mSubeshes = new List<Submesh>();
            mTextures = new Dictionary<string, ITexture>();
            mMaterials = new List<Material>();
            mVertices = new List<ModelVertex>();
            mIndices = new List<uint>();

            if (!Matrix4x4.Invert(mScene->MRootNode->MTransformation, out mInverseRootTransform))
            {
                mInverseRootTransform = Matrix4x4.Identity;
            }

            // load vertices, materials, bones, etc
            Load();

            mVertexBuffer = mImportContext.CreateVertexBuffer(mVertices);
            mIndexBuffer = mImportContext.CreateIndexBuffer(mIndices);

            if (mImportContext.ShouldCopyPostLoad)
            {
                mImportContext.CopyBuffers();
            }
        }

        private unsafe void Load()
        {
            using var loadEvent = OptickMacros.Event();

            ProcessNode(mScene->MRootNode, mInverseRootTransform);
            for (uint i = 0; i < mScene->MNumMaterials; i++)
            {
                var assimpMaterial = mScene->MMaterials[i];
                var material = ProcessMaterial(assimpMaterial);
                
                mMaterials.Add(material);
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

        private unsafe void Dispose(bool disposing)
        {
            using var disposeEvent = OptickMacros.Event();
            if (disposing)
            {
                mVertexBuffer.Dispose();
                mIndexBuffer.Dispose();
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

        private unsafe Node* FindNode(string name, Node* currentNode = null)
        {
            using var findNodeEvent = OptickMacros.Event();

            var node = currentNode is null ? mScene->MRootNode : currentNode;
            if (node->MName == name)
            {
                return node;
            }

            for (uint i = 0; i < node->MNumChildren; i++)
            {
                var foundNode = FindNode(name, node->MChildren[i]);
                if (foundNode is not null)
                {
                    return foundNode;
                }
            }

            return null;
        }

        private static unsafe Matrix4x4 CalculateAbsoluteTransform(Node* node)
        {
            using var calculateEvent = OptickMacros.Event();

            var transform = node->MTransformation;
            var currentParent = node->MParent;

            while (currentParent is not null)
            {
                transform = currentParent->MTransformation * transform;
                currentParent = currentParent->MParent;
            }

            return transform;
        }

        private unsafe bool ProcessBones(Mesh* mesh, int vertexOffset)
        {
            using var processBonesEvent = OptickMacros.Event();
            if (mesh->MNumBones == 0)
            {
                return false;
            }

            var nodeMap = new Dictionary<nint, int>();
            var processedBones = new List<ProcessedBone>();

            for (uint i = 0; i < mesh->MNumBones; i++)
            {
                var bone = mesh->MBones[i];
                var name = bone->MName.ToString();

                var node = FindNode(name, bone->MArmature);
                if (node is null)
                {
                    throw new InvalidOperationException($"Failed to find node for bone: {name}");
                }

                int index = processedBones.Count;
                processedBones.Add(new ProcessedBone
                {
                    InitialIndex = index,
                    Parent = -1,
                    Name = name,
                    Node = node,
                    Bone = bone
                });

                nodeMap.Add((nint)node, index);
            }

            for (int i = 0; i < processedBones.Count; i++)
            {
                var bone = processedBones[i];
                var node = bone.Node;

                if (nodeMap.TryGetValue((nint)node->MParent, out int parentIndex))
                {
                    bone.Parent = parentIndex;
                    processedBones[i] = bone;
                }
            }

            processedBones.Sort((a, b) => a.Parent.CompareTo(b.Parent));
            var idMap = new Dictionary<int, int>();
            for (int i = 0; i < processedBones.Count; i++)
            {
                var bone = processedBones[i];
                idMap.Add(bone.InitialIndex, i);
            }

            int boneOffset = (mSkeleton ??= new Skeleton()).BoneCount;
            foreach (var bone in processedBones)
            {
                int parent = bone.Parent;
                Matrix4x4 parentTransform = Matrix4x4.Identity;

                if (parent >= 0)
                {
                    parent = idMap[parent] + boneOffset;
                }
                else
                {
                    parentTransform = mInverseRootTransform;
                    if (bone.Node->MParent is not null)
                    {
                        parentTransform *= CalculateAbsoluteTransform(bone.Node->MParent);
                    }
                }

                int boneId = mSkeleton.AddBone(parent, bone.Name, bone.Node->MTransformation, bone.Bone->MOffsetMatrix, parentTransform);
                for (uint j = 0; j < bone.Bone->MNumWeights; j++)
                {
                    var weight = bone.Bone->MWeights[j];
                    var vertex = mVertices[(int)weight.MVertexId + vertexOffset];

                    int boneLimit = mImportContext.MaxBonesPerVertex;
                    if (boneLimit > 0 && vertex.Bones.Count >= boneLimit)
                    {
                        throw new InvalidOperationException($"Bone limit per vertex of {boneLimit} exceeded!");
                    }

                    vertex.Bones.Add(new BoneData
                    {
                        Index = boneId,
                        Weight = weight.MWeight
                    });
                }
            }
            
            return true;
        }

        private unsafe Submesh ProcessMesh(Node* node, Mesh* mesh, Matrix4x4 transform)
        {
            using var processMeshEvent = OptickMacros.Event();
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

                var textureCoords = mesh->MTextureCoords[0];
                var uv = textureCoords is null ? new Vector3(0f) : textureCoords[i];

                mVertices.Add(new ModelVertex
                {
                    Position = position,
                    Normal = normal,
                    UV = new Vector2(uv.X, uv.Y),
                    Bones = new List<BoneData>()
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
            submesh.HasBones = ProcessBones(mesh, submesh.VertexOffset);

            return submesh;
        }

        private unsafe void ProcessNode(Node* node, Matrix4x4 parentTransform)
        {
            using var processNodeEvent = OptickMacros.Event();

            var nodeTransform = node->MTransformation;
            var transform = parentTransform * nodeTransform;

            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                uint meshIndex = node->MMeshes[i];
                var mesh = mScene->MMeshes[meshIndex];

                var submesh = ProcessMesh(node, mesh, transform);
                mSubeshes.Add(submesh);
            }

            for (uint i = 0; i < node->MNumChildren; i++)
            {
                var child = node->MChildren[i];
                ProcessNode(child, transform);
            }
        }

        private unsafe void ProcessMaterialTextures(Silk.NET.Assimp.Material* assimpMaterial, Material material)
        {
            using var processTexturesEvent = OptickMacros.Event();

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
            using var processMaterialEvent = OptickMacros.Event();

            AssimpString name;
            if (sAPI.GetMaterialString(assimpMaterial, "$mat.name", 0, 0, &name) != Return.Success)
            {
                name = "Material";
            }

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

            var data = new byte[4];
            Array.Fill(data, byte.MaxValue);
            
            mWhiteTexture ??= mImportContext.CreateTexture(data, 1, 1, DeviceImageFormat.RGBA8_UNORM);
            var material = new Material(name.ToString(), mWhiteTexture, mImportContext.GraphicsContext);

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

        public void GetMeshData(out Vector3[] vertices, out int[] indices)
        {
            using var getMeshDataEvent = OptickMacros.Event();

            vertices = mVertices.Select(vertex => vertex.Position).ToArray();
            indices = mIndices.Select(index => (int)index).ToArray();
        }

        public IReadOnlyList<Submesh> Submeshes => mSubeshes;
        public IReadOnlyList<Material> Materials => mMaterials;
        public IDeviceBuffer VertexBuffer => mVertexBuffer!;
        public IDeviceBuffer IndexBuffer => mIndexBuffer!;
        public Skeleton? Skeleton => mSkeleton;

        private readonly unsafe Scene* mScene;
        private readonly string mPath;
        private readonly bool mLoadedFromFile;
        private readonly IModelImportContext mImportContext;
        private readonly Matrix4x4 mInverseRootTransform;

        private readonly List<Submesh> mSubeshes;
        private readonly Dictionary<string, ITexture> mTextures;
        private readonly List<Material> mMaterials;
        private readonly List<ModelVertex> mVertices;
        private readonly List<uint> mIndices;

        private IDeviceBuffer mVertexBuffer, mIndexBuffer;
        private ITexture? mWhiteTexture;
        private Skeleton? mSkeleton;

        private bool mDisposed;
    }
}
