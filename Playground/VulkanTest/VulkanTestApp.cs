using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Vulkan;
using Silk.NET.Assimp;
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
    }

    internal struct PipelineSpecification : IPipelineSpecification
    {
        public PipelineBlendMode BlendMode { get; set; }
        public PipelineFrontFace FrontFace { get; set; }
        public bool EnableDepthTesting { get; set; }
        public bool DisableCulling { get; set; }
    }

    internal struct UniformBufferData
    {
        public Matrix4x4 ViewProjection;
    }

    internal struct PushConstantData
    {
        public Matrix4x4 Model;
        public Vector4 Color;
    }

    internal struct QuadDirection
    {
        public Vector3 Direction { get; set; }
        public Vector3[] OtherDirections { get; set; }
    }

    internal sealed class ModelImportContext : IModelImportContext
    {
        public ModelImportContext(IGraphicsContext context)
        {
            mContext = context;
            mStagingBuffers = new List<IDeviceBuffer>();
            mCommandList = null;
        }

        public bool ShouldCopyPostLoad => true;

        public bool FlipUVs => true;
        public bool LeftHanded => true;

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

        public IDeviceBuffer CreateStaticVertexBuffer(IReadOnlyList<StaticModelVertex> vertices)
        {
            BeginCommandList();

            var bufferVertices = new Vertex[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                bufferVertices[i] = new Vertex
                {
                    Position = vertices[i].Position,
                    Normal = vertices[i].Normal,
                    UV = vertices[i].UV
                };
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

        public ITexture LoadTexture(string texturePath, string modelPath, bool loadedFromFile)
        {
            throw new NotImplementedException();
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

        [EventHandler(nameof(Load))]
        private void OnLoad()
        {
            var arguments = CommandLineArguments;
            if (arguments.Length == 0)
            {
                throw new ArgumentException("No model specified!");
            }

            CreateGraphicsContext<VulkanContext>();

            var context = GraphicsContext!;
            var swapchain = context.Swapchain;
            swapchain.VSync = true; // enable vsync

            mShaderLibrary = new ShaderLibrary(this);
            mRenderer = context.CreateRenderer();

            mPipeline = mShaderLibrary.LoadPipeline<TestShader>(new PipelineDescription
            {
                RenderTarget = swapchain.RenderTarget,
                Type = PipelineType.Graphics,
                FrameCount = swapchain.FrameCount,
                Specification = new PipelineSpecification
                {
                    FrontFace = PipelineFrontFace.CounterClockwise,
                    BlendMode = PipelineBlendMode.Default,
                    EnableDepthTesting = true,
                    DisableCulling = false
                }
            });

            var resourceName = nameof(TestShader.u_CameraBuffer);
            int uniformBufferSize = mPipeline.GetBufferSize(resourceName);

            if (uniformBufferSize < 0)
            {
                throw new ArgumentException($"Failed to find buffer \"{resourceName}\"");
            }

            mUniformBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, uniformBufferSize);
            if (!mPipeline.Bind(mUniformBuffer, resourceName, 0))
            {
                throw new InvalidOperationException("Failed to bind buffer!");
            }

            mModel = Model.Load(arguments[0], new ModelImportContext(context));
        }

        [EventHandler(nameof(Closing))]
        private void OnClose()
        {
            var device = GraphicsContext?.Device;
            device?.ClearQueues();

            mPipeline?.Dispose();
            mUniformBuffer?.Dispose();
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
        private void OnUpdate(double delta)
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

            mUniformBuffer!.MapStructure(mPipeline!, nameof(TestShader.u_CameraBuffer), new UniformBufferData
            {
                ViewProjection = Matrix4x4.Transpose(projection * view)
            });
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

            mPipeline!.Bind(renderInfo.CommandList, renderInfo.CurrentFrame);
            mModel!.VertexBuffer.BindVertices(renderInfo.CommandList, 0);
            mModel!.IndexBuffer.BindIndices(renderInfo.CommandList, DeviceBufferIndexType.UInt32);

            var submeshes = mModel!.Submeshes;
            for (int i = 0; i < submeshes.Count; i++)
            {
                var submesh = submeshes[i];
                mPipeline!.PushConstants(renderInfo.CommandList, mapped =>
                {
                    mPipeline!.MapStructure(mapped, nameof(TestShader.u_PushConstants), new PushConstantData
                    {
                        Model = Matrix4x4.CreateRotationX(MathF.PI / 2f) * submesh.Transform,
                        Color = new Vector4(new Vector3((i + 1) / (float)submeshes.Count), 1f)
                    });
                });

                mRenderer!.RenderIndexed(renderInfo.CommandList, submesh.IndexOffset, submesh.IndexCount);
            }

            renderInfo.RenderTarget.EndRender(renderInfo.CommandList);
        }

        private ShaderLibrary? mShaderLibrary;
        private IPipeline? mPipeline;
        private IDeviceBuffer? mUniformBuffer;
        private Model? mModel;
        private IRenderer? mRenderer;
        private float mTime;
    }
}
