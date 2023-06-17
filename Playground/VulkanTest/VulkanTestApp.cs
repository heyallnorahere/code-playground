using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Vulkan;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using VulkanTest.Shaders;

namespace VulkanTest
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public int BoneCount;
        public unsafe fixed int BoneIDs[ModelImportContext.BoneLimitPerVertex];
        public unsafe fixed float BoneWeights[ModelImportContext.BoneLimitPerVertex];
    }

    internal struct PipelineSpecification : IPipelineSpecification
    {
        public PipelineBlendMode BlendMode { get; set; }
        public PipelineFrontFace FrontFace { get; set; }
        public bool EnableDepthTesting { get; set; }
        public bool DisableCulling { get; set; }
    }

    internal struct CameraBufferData
    {
        public Matrix4x4 ViewProjection;
    }

    internal struct PushConstantData
    {
        public Matrix4x4 Model;
        public int BoneTransformOffset;
    }

    internal struct QuadDirection
    {
        public Vector3 Direction { get; set; }
        public Vector3[] OtherDirections { get; set; }
    }

    internal sealed class ModelImportContext : IModelImportContext
    {
        public const int BoneLimitPerVertex = 4;

        public ModelImportContext(IGraphicsContext context)
        {
            mContext = context;
            mStagingBuffers = new List<IDeviceBuffer>();
            mCommandList = null;
        }

        public IGraphicsContext GraphicsContext => mContext;

        public bool ShouldCopyPostLoad => true;
        public bool FlipUVs => true;
        public bool LeftHanded => true;
        public int MaxBonesPerVertex => BoneLimitPerVertex;

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

        public unsafe IDeviceBuffer CreateVertexBuffer(IReadOnlyList<ModelVertex> vertices)
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

        public IDeviceBuffer CreateIndexBuffer(IReadOnlyList<uint> indices)
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

        public ITexture CreateTexture(ReadOnlySpan<byte> data, int width, int height, DeviceImageFormat format)
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

        public ITexture LoadTexture(string texturePath, string modelPath, bool loadedFromFile, ISamplerSettings samplerSettings)
        {
            Image<Rgba32> image;
            if (loadedFromFile)
            {
                var path = Path.IsPathFullyQualified(texturePath) ? texturePath : Path.GetFullPath(Path.Join(Path.GetDirectoryName(modelPath), texturePath));
                image = Image.Load<Rgba32>(path);
            }
            else
            {
                if (Path.IsPathFullyQualified(texturePath))
                {
                    throw new ArgumentException("Cannot load a fully qualified path!");
                }

                var relativePath = Path.Join(Path.GetDirectoryName(modelPath), texturePath).Replace('\\', '/').Replace("./", string.Empty);

                int directoryEscapePosition;
                while ((directoryEscapePosition = relativePath.LastIndexOf("..")) > 0)
                {
                    int separatorPosition = relativePath.LastIndexOf("/", directoryEscapePosition - 2);
                    if (separatorPosition < 0)
                    {
                        separatorPosition = 0;
                    }

                    relativePath = relativePath.Remove(separatorPosition, directoryEscapePosition + 2 - separatorPosition);
                }

                var stream = VulkanTestApp.GetResourceStream(relativePath);
                image = Image.Load<Rgba32>(stream ?? throw new FileNotFoundException());
            }

            var deviceImage = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = image.Size,
                Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = DeviceImageFormat.RGBA8_SRGB
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

        public void CopyBuffers()
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

        private ICommandList? mCommandList;
        private readonly List<IDeviceBuffer> mStagingBuffers;
        private readonly IGraphicsContext mContext;
    }

    [ApplicationTitle("Vulkan Test")]
    [ApplicationGraphicsAPI(AppGraphicsAPI.Vulkan)]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    [VulkanAPIVersion("1.3")]
    public class VulkanTestApp : GraphicsApplication
    {
        public VulkanTestApp()
        {
            Utilities.BindHandlers(this, this);
        }

        public static Stream? GetResourceStream(string path)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceId = $"{assembly.GetName().Name}.Resources.{path.Replace('\\', '/').Replace('/', '.')}";
            return assembly.GetManifestResourceStream(resourceId);
        }

        private static Model? LoadModelResource(string path, IModelImportContext importContext)
        {
            var stream = GetResourceStream(path);
            if (stream is null)
            {
                return null;
            }

            var buffer = new byte[stream.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                totalRead += stream.Read(buffer, totalRead, buffer.Length - totalRead);
            }

            return Model.Load(buffer, path, importContext);
        }

        [EventHandler(nameof(Load))]
        private void OnLoad()
        {
            CreateGraphicsContext<VulkanContext>();

            var context = GraphicsContext!;
            var swapchain = context.Swapchain;
            swapchain.VSync = true; // enable vsync

            mShaderLibrary = new ShaderLibrary(this);
            mRenderer = context.CreateRenderer();

            mModel = LoadModelResource("Models/rigged-character.fbx", new ModelImportContext(context));
            if (mModel is null)
            {
                throw new InvalidOperationException("Failed to load model!");
            }

            mPipelines = new IPipeline[mModel.Materials.Count];
            string cameraBufferName = nameof(TestShader.u_CameraBuffer);
            string boneTransformBufferName = nameof(TestShader.u_BoneTransformBuffer);

            for (int i = 0; i < mModel.Materials.Count; i++)
            {
                var material = mModel.Materials[i];
                var pipeline = mShaderLibrary.LoadPipeline<TestShader>(new PipelineDescription
                {
                    RenderTarget = swapchain.RenderTarget,
                    Type = PipelineType.Graphics,
                    FrameCount = swapchain.FrameCount,
                    Specification = material.PipelineSpecification
                });

                if (mCameraBuffer is null)
                {
                    int cameraBufferSize = pipeline.GetBufferSize(cameraBufferName);
                    if (cameraBufferSize < 0)
                    {
                        throw new ArgumentException($"Failed to find buffer \"{cameraBufferName}\"");
                    }

                    mCameraBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, cameraBufferSize);
                }

                if (mBoneTransformBuffer is null)
                {
                    int boneTransformBufferSize = pipeline.GetBufferSize(boneTransformBufferName);
                    if (boneTransformBufferSize < 0)
                    {
                        throw new ArgumentException($"Failed to find buffer \"{boneTransformBufferName}\"");
                    }

                    mBoneTransformBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, boneTransformBufferSize);
                }

                pipeline.Bind(mCameraBuffer, cameraBufferName, 0);
                pipeline.Bind(mBoneTransformBuffer, boneTransformBufferName, 0);
                material.Bind(pipeline, nameof(TestShader.u_MaterialBuffer), textureType => $"u_{textureType}Map");

                mPipelines[i] = pipeline;
            }
        }

        [EventHandler(nameof(Closing))]
        private void OnClose()
        {
            var device = GraphicsContext?.Device;
            device?.ClearQueues();

            if (mPipelines is not null)
            {
                for (int i = 0; i < mPipelines.Length; i++)
                {
                    mPipelines[i].Dispose();
                }
            }

            mCameraBuffer?.Dispose();
            mBoneTransformBuffer?.Dispose();
            mModel?.Dispose();

            mShaderLibrary?.Dispose();
            GraphicsContext?.Dispose();
        }

        // https://computergraphics.stackexchange.com/questions/12448/vulkan-perspective-matrix-vs-opengl-perspective-matrix
        // https://github.com/g-truc/glm/blob/efec5db081e3aad807d0731e172ac597f6a39447/glm/ext/matrix_clip_space.inl#L265
        /// <summary>
        /// Left-handed, depth 0-1
        /// </summary>
        private static Matrix4x4 Perspective(float verticalFov, float aspectRatio, float nearPlane, float farPlane)
        {
            float g = 1f / MathF.Tan(verticalFov / 2f);
            float k = farPlane / (farPlane - nearPlane);

            return new Matrix4x4(g / aspectRatio, 0f, 0f, 0f,
                                 0f, g, 0f, 0f,
                                 0f, 0f, k, -nearPlane * k,
                                 0f, 0f, 1f, 0f);

            /* GLM code
            assert(abs(aspect - std::numeric_limits<T>::epsilon()) > static_cast<T>(0));

		    T const tanHalfFovy = tan(fovy / static_cast<T>(2));

		    mat<4, 4, T, defaultp> Result(static_cast<T>(0));
		    Result[0][0] = static_cast<T>(1) / (aspect * tanHalfFovy);
		    Result[1][1] = static_cast<T>(1) / (tanHalfFovy);
		    Result[2][2] = zFar / (zFar - zNear);
		    Result[2][3] = static_cast<T>(1);
		    Result[3][2] = -(zFar * zNear) / (zFar - zNear);
		    return Result;
            */
        }

        // https://github.com/g-truc/glm/blob/efec5db081e3aad807d0731e172ac597f6a39447/glm/ext/matrix_transform.inl#L176
        /// <summary>
        /// Left-handed
        /// </summary>
        private static Matrix4x4 LookAt(Vector3 eye, Vector3 center, Vector3 up)
        {
            var direction = Vector3.Normalize(center - eye);
            var right = Vector3.Normalize(Vector3.Cross(up, direction));
            var crossUp = Vector3.Cross(direction, right);

            return new Matrix4x4(right.X, right.Y, right.Z, -Vector3.Dot(right, eye),
                                 crossUp.X, crossUp.Y, crossUp.Z, -Vector3.Dot(crossUp, eye),
                                 direction.X, direction.Y, direction.Z, -Vector3.Dot(direction, eye),
                                 0f, 0f, 0f, 1f);

            /* GLM code
            vec<3, T, Q> const f(normalize(center - eye));
		    vec<3, T, Q> const s(normalize(cross(up, f)));
		    vec<3, T, Q> const u(cross(f, s));

		    mat<4, 4, T, Q> Result(1);
		    Result[0][0] = s.x;
		    Result[1][0] = s.y;
		    Result[2][0] = s.z;
		    Result[0][1] = u.x;
		    Result[1][1] = u.y;
		    Result[2][1] = u.z;
		    Result[0][2] = f.x;
		    Result[1][2] = f.y;
		    Result[2][2] = f.z;
		    Result[3][0] = -dot(s, eye);
		    Result[3][1] = -dot(u, eye);
		    Result[3][2] = -dot(f, eye);
		    return Result;
            */
        }

        [EventHandler(nameof(Update))]
        private unsafe void OnUpdate(double delta)
        {
            mTime += (float)delta;

            var swapchain = GraphicsContext?.Swapchain;
            if (swapchain is null)
            {
                return;
            }

            const float radius = 5f;
            float x = -MathF.Cos(mTime) * radius;
            float z = MathF.Sin(mTime) * radius;

            float aspectRatio = swapchain.Width / (float)swapchain.Height;
            var projection = Perspective(MathF.PI / 4f, aspectRatio, 0.1f, 100f);
            var view = LookAt(new Vector3(x, radius, z), Vector3.Zero, Vector3.UnitY);

            mCameraBuffer!.MapStructure(mPipelines![0], nameof(TestShader.u_CameraBuffer), new CameraBufferData
            {
                ViewProjection = Matrix4x4.Transpose(projection * view)
            });

            // todo: compute via animationcontroller or something
            var skeleton = mModel?.Skeleton;
            if (skeleton is not null)
            {
                if (skeleton.BoneCount > TestShader.MaxBones)
                {
                    throw new InvalidOperationException("Skeleton has more bones than is supported!");
                }

                var transforms = new List<Matrix4x4>();
                var result = new Matrix4x4[skeleton.BoneCount];

                for (int i = 0; i < skeleton.BoneCount; i++)
                {
                    int parent = skeleton.GetParent(i);
                    var parentTransform = parent < 0 ? skeleton.GetParentTransform(i) : transforms[parent];

                    var nodeTransform = skeleton.GetTransform(i);
                    var globalTransform = parentTransform * nodeTransform;
                    var boneTransform = globalTransform * skeleton.GetOffsetMatrix(i);

                    transforms.Add(globalTransform);
                    result[i] = Matrix4x4.Transpose(boneTransform);
                }

                mBoneTransformBuffer!.CopyFromCPU(result);
            }
        }

        [EventHandler(nameof(Render))]
        private void OnRender(FrameRenderInfo renderInfo)
        {
            if (renderInfo.CommandList is null || renderInfo.RenderTarget is null || renderInfo.Framebuffer is null)
            {
                return;
            }

            var clearColor = new Vector4(0f, 0f, 0f, 1f);
            renderInfo.RenderTarget.BeginRender(renderInfo.CommandList, renderInfo.Framebuffer, clearColor, true);

            mModel!.VertexBuffer.BindVertices(renderInfo.CommandList, 0);
            mModel!.IndexBuffer.BindIndices(renderInfo.CommandList, DeviceBufferIndexType.UInt32);

            var model = Matrix4x4.CreateScale(0.001f);
            var submeshes = mModel!.Submeshes;
            for (int i = 0; i < submeshes.Count; i++)
            {
                var submesh = submeshes[i];
                var pipeline = mPipelines![submesh.MaterialIndex];

                pipeline.Bind(renderInfo.CommandList, renderInfo.CurrentFrame);
                pipeline.PushConstants(renderInfo.CommandList, mapped =>
                {
                    pipeline.MapStructure(mapped, nameof(TestShader.u_PushConstants), new PushConstantData
                    {
                        Model = Matrix4x4.Transpose(model * submesh.Transform),
                        BoneTransformOffset = 0
                    });
                });

                mRenderer!.RenderIndexed(renderInfo.CommandList, submesh.IndexOffset, submesh.IndexCount);
            }

            renderInfo.RenderTarget.EndRender(renderInfo.CommandList);
        }

        private ShaderLibrary? mShaderLibrary;
        private IPipeline[]? mPipelines;
        private IDeviceBuffer? mCameraBuffer, mBoneTransformBuffer;
        private Model? mModel;
        private IRenderer? mRenderer;
        private float mTime;
    }
}
