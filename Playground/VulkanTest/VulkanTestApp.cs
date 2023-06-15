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

    internal struct QuadDirection
    {
        public Vector3 Direction { get; set; }
        public Vector3[] OtherDirections { get; set; }
    }

    [ApplicationTitle("Vulkan Test")]
    [ApplicationGraphicsAPI(AppGraphicsAPI.Vulkan)]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    [VulkanAPIVersion("1.3")]
    public class VulkanTestApp : GraphicsApplication
    {
        public const PipelineFrontFace FrontFace = PipelineFrontFace.CounterClockwise;

        private static readonly Vertex[] sVertices;
        private static readonly uint[] sIndices;

        static VulkanTestApp()
        {
            var vertices = new Vertex[]
            {
                // quad
                new Vertex
                {
                    Position = new Vector3(0.5f, -0.5f, 0f),
                    Normal = -Vector3.UnitZ,
                    UV = new Vector2(1f, 1f),
                },
                new Vertex
                {
                    Position = new Vector3(-0.5f, -0.5f, 0f),
                    Normal = -Vector3.UnitZ,
                    UV = new Vector2(0f, 1f),
                },
                new Vertex
                {
                    Position = new Vector3(-0.5f, 0.5f, 0f),
                    Normal = -Vector3.UnitZ,
                    UV = new Vector2(0f, 0f),
                },
                new Vertex
                {
                    Position = new Vector3(0.5f, 0.5f, 0f),
                    Normal = -Vector3.UnitZ,
                    UV = new Vector2(1f, 0f),
                }
            };

            var positionValues = new float[]
            {
                0f, -1f, 1f
            };

            var counterClockwiseIndices = new int[]
            {
                0, 3, 1,
                1, 3, 2
            };

            var clockwiseIndices = new int[]
            {
                0, 1, 3,
                1, 2, 3
            };

            var indices = FrontFace == PipelineFrontFace.Clockwise ? clockwiseIndices : counterClockwiseIndices;
            var vertexData = new List<Vertex>();
            var indexData = new List<int>();

            foreach (float positionValue in positionValues)
            {
                foreach (int index in indices)
                {
                    indexData.Add(index + vertexData.Count);
                }

                var offset = (Vector3.UnitX + Vector3.UnitZ) * positionValue / 2f;
                foreach (var vertex in vertices)
                {
                    vertexData.Add(new Vertex
                    {
                        Position = vertex.Position + offset,
                        Normal = vertex.Normal,
                        UV = vertex.UV
                    });
                }
            }

            sVertices = vertexData.ToArray();
            sIndices = indexData.Select(index => (uint)index).ToArray();
        }

        private Image<T> LoadImage<T>(Assembly assembly, string resourceName) where T : unmanaged, IPixel<T>
        {
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                throw new FileNotFoundException();
            }

            return Image.Load<T>(stream);
        }

        public VulkanTestApp()
        {
            Utilities.BindHandlers(this, this);
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

            mPipeline = mShaderLibrary.LoadPipeline<TestShader>(new PipelineDescription
            {
                RenderTarget = swapchain.RenderTarget,
                Type = PipelineType.Graphics,
                FrameCount = swapchain.FrameCount,
                Specification = new PipelineSpecification
                {
                    FrontFace = FrontFace,
                    BlendMode = PipelineBlendMode.SourceAlphaOneMinusSourceAlpha,
                    EnableDepthTesting = true,
                    DisableCulling = true
                }
            });

            var resourceName = nameof(TestShader.u_CameraBuffer);
            int uniformBufferSize = mPipeline.GetBufferSize(resourceName);

            if (uniformBufferSize < 0)
            {
                throw new ArgumentException($"Failed to find buffer \"{resourceName}\"");
            }

            int vertexBufferSize = sVertices.Length * Marshal.SizeOf<Vertex>();
            int indexBufferSize = sIndices.Length * Marshal.SizeOf<uint>();

            using var vertexStagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, vertexBufferSize);
            using var indexStagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, indexBufferSize);

            vertexStagingBuffer.CopyFromCPU(sVertices);
            indexStagingBuffer.CopyFromCPU(sIndices);

            mVertexBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Vertex, vertexBufferSize);
            mIndexBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Index, indexBufferSize);
            mUniformBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, uniformBufferSize);

            var transferQueue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = transferQueue.Release();
            commandList.Begin();

            vertexStagingBuffer.CopyBuffers(commandList, mVertexBuffer, vertexBufferSize);
            indexStagingBuffer.CopyBuffers(commandList, mIndexBuffer, indexBufferSize);

            var image = LoadImage<Rgba32>(GetType().Assembly, "VulkanTest.Resources.Textures.disaster.png");
            mTexture = context.LoadTexture(image, DeviceImageFormat.RGBA8_SRGB, commandList, out IDeviceBuffer imageStagingBuffer);

            commandList.End();
            transferQueue.Submit(commandList, true);

            if (!mPipeline.Bind(mTexture, nameof(TestShader.u_Texture), 0))
            {
                throw new InvalidOperationException("Failed to bind texture!");
            }

            if (!mPipeline.Bind(mUniformBuffer, resourceName, 0))
            {
                throw new InvalidOperationException("Failed to bind buffer!");
            }

            imageStagingBuffer.Dispose();
        }

        [EventHandler(nameof(Closing))]
        private void OnClose()
        {
            var device = GraphicsContext?.Device;
            device?.ClearQueues();

            mPipeline?.Dispose();
            mVertexBuffer?.Dispose();
            mIndexBuffer?.Dispose();
            mUniformBuffer?.Dispose();
            mTexture?.Dispose();

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
            var swapchain = GraphicsContext?.Swapchain;
            if (swapchain is null)
            {
                return;
            }

            float aspectRatio = swapchain.Width / (float)swapchain.Height;
            var projection = Perspective(MathF.PI / 4f, aspectRatio, 0.1f, 100f);
            var view = LookAt(Vector3.UnitZ * -2f, Vector3.Zero, Vector3.UnitY);

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

            mVertexBuffer!.BindVertices(renderInfo.CommandList, 0);
            mIndexBuffer!.BindIndices(renderInfo.CommandList, DeviceBufferIndexType.UInt32);
            mPipeline!.Bind(renderInfo.CommandList, renderInfo.CurrentFrame);
            mRenderer!.RenderIndexed(renderInfo.CommandList, sIndices.Length);

            renderInfo.RenderTarget.EndRender(renderInfo.CommandList);
        }

        private ShaderLibrary? mShaderLibrary;
        private IPipeline? mPipeline;
        private IDeviceBuffer? mVertexBuffer, mIndexBuffer, mUniformBuffer;
        private ITexture? mTexture;
        private IRenderer? mRenderer;
    }
}
