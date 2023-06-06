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
        public int Transform;
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
        public Matrix4x4 ModelViewProjection;
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
            var directions = new QuadDirection[]
            {
                new QuadDirection
                {
                    Direction = Vector3.UnitX,
                    OtherDirections = new Vector3[]
                    {
                        Vector3.UnitY,
                        Vector3.UnitZ,
                    }
                },
                new QuadDirection
                {
                    Direction = Vector3.UnitY,
                    OtherDirections = new Vector3[]
                    {
                        Vector3.UnitZ,
                        Vector3.UnitX
                    }
                },
                new QuadDirection
                {
                    Direction = Vector3.UnitZ,
                    OtherDirections = new Vector3[]
                    {
                        Vector3.UnitX,
                        Vector3.UnitY
                    }
                }
            };

            var counterClockwiseTemplateIndices = new int[]
            {
                0, 3, 1,
                1, 3, 2
            };

            var clockwiseTemplateIndices = new int[]
            {
                0, 1, 3,
                1, 2, 3
            };

            var faceDirections = new List<int>();
            for (int i = 0; i < directions.Length; i++)
            {
                int value = i + 1;
                faceDirections.Add(value);
                faceDirections.Add(-value);
            }

            var vertices = new List<Vertex>
            {
                // quad
                new Vertex
                {
                    Position = new Vector3(0.5f, -0.5f, 0f),
                    Normal = new Vector3(0f, 0f, -1f),
                    UV = new Vector2(0f, 1f),
                    Transform = 0
                },
                new Vertex
                {
                    Position = new Vector3(-0.5f, -0.5f, 0f),
                    Normal = new Vector3(0f, 0f, 1f),
                    UV = new Vector2(1f, 1f),
                    Transform = 0
                },
                new Vertex
                {
                    Position = new Vector3(-0.5f, 0.5f, 0f),
                    Normal = new Vector3(1f, 0f, 0f),
                    UV = new Vector2(1f, 0f),
                    Transform = 0
                },
                new Vertex
                {
                    Position = new Vector3(0.5f, 0.5f, 0f),
                    Normal = new Vector3(-1f, 0f, 0f),
                    UV = new Vector2(0f, 0f),
                    Transform = 0
                }
            };

            var templateIndices = FrontFace == PipelineFrontFace.Clockwise ? clockwiseTemplateIndices : counterClockwiseTemplateIndices;
            var indices = new List<int>();

            foreach (var directionValue in faceDirections)
            {
                var abs = Math.Abs(directionValue);
                var index = abs - 1;

                var factor = directionValue / abs;
                var direction = directions[index];
                var directionVector = direction.Direction * factor;
                var otherDirections = direction.OtherDirections;

                var vertexPositions = new Vertex[]
                {
                    new Vertex
                    {
                        Position = otherDirections[0] * factor + otherDirections[1],
                        UV = new Vector2(1f, 1f)
                    },
                    new Vertex
                    {
                        Position = -otherDirections[0] * factor + otherDirections[1],
                        UV = new Vector2(0f, 1f)
                    },
                    new Vertex
                    {
                        Position = -otherDirections[0] * factor - otherDirections[1],
                        UV = new Vector2(0f, 0f)
                    },
                    new Vertex
                    {
                        Position = otherDirections[0] * factor - otherDirections[1],
                        UV = new Vector2(1f, 0f)
                    }
                };

                int indexOffset = vertices.Count;
                foreach (var vertex in vertexPositions)
                {
                    vertices.Add(new Vertex
                    {
                        Position = (vertex.Position + directionVector) / 2f,
                        Normal = directionVector,
                        UV = vertex.UV,
                        Transform = 1
                    });
                }

                foreach (var templateIndex in templateIndices)
                {
                    indices.Add(templateIndex + indexOffset);
                }
            }

            sVertices = vertices.ToArray();
            sIndices = indices.Select(index => (uint)index).ToArray();
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
                    BlendMode = PipelineBlendMode.None,
                    EnableDepthTesting = true,
                    DisableCulling = true
                }
            });

            var resourceName = nameof(TestShader.u_UniformBuffer);
            int bufferSize = mPipeline.GetBufferSize(resourceName);

            if (bufferSize < 0)
            {
                throw new ArgumentException($"Failed to find buffer \"{resourceName}\"");
            }

            mUniformBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, bufferSize);

            int vertexBufferSize = sVertices.Length * Marshal.SizeOf<Vertex>();
            int indexBufferSize = sIndices.Length * Marshal.SizeOf<uint>();

            var image = LoadImage<Rgba32>(GetType().Assembly, "VulkanTest.Resources.Textures.disaster.png");
            int imageBufferSize = image.Width * image.Height * Marshal.SizeOf<Rgba32>();

            using var vertexStagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, vertexBufferSize);
            using var indexStagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, indexBufferSize);
            using var imageStagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, imageBufferSize);

            vertexStagingBuffer.CopyFromCPU(sVertices);
            indexStagingBuffer.CopyFromCPU(sIndices);

            var imageDataBuffer = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(imageDataBuffer);
            imageStagingBuffer.CopyFromCPU(imageDataBuffer);

            mVertexBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Vertex, vertexBufferSize);
            mIndexBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Index, indexBufferSize);

            mTexture = context.CreateDeviceImage(new DeviceImageInfo
            {
                Size = image.Size,
                Usage = DeviceImageUsageFlags.Render | DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination,
                Format = DeviceImageFormat.RGBA8_UNORM
            }).CreateTexture(true);

            var copyLayout = mTexture.Image.GetLayout(DeviceImageLayoutName.CopyDestination);
            var renderLayout = mTexture.Image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

            var transferQueue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = transferQueue.Release();
            commandList.Begin();

            vertexStagingBuffer.CopyBuffers(commandList, mVertexBuffer, vertexBufferSize);
            indexStagingBuffer.CopyBuffers(commandList, mIndexBuffer, indexBufferSize);

            mTexture.Image.TransitionLayout(commandList, mTexture.Image.Layout, copyLayout);
            mTexture.Image.CopyFromBuffer(commandList, imageStagingBuffer, copyLayout);
            mTexture.Image.TransitionLayout(commandList, copyLayout, renderLayout);

            commandList.End();
            transferQueue.Submit(commandList, true);

            mTexture.Image.Layout = renderLayout;
            if (!mPipeline.Bind(mTexture, nameof(TestShader.u_Texture), 0))
            {
                throw new InvalidOperationException("Failed to bind texture!");
            }

            if (!mPipeline.Bind(mUniformBuffer, resourceName, 0))
            {
                throw new InvalidOperationException("Failed to bind buffer!");
            }
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

        private static Vector4 Multiply(Matrix4x4 lhs, Vector4 rhs)
        {
            var result = Vector4.Zero;
            for (int c = 0; c < 4; c++)
            {
                for (int r = 0; r < 4; r++)
                {
                    result[c] += rhs[r] * lhs[r, c];
                }
            }

            return result;
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

            float radius = 5f;
            float x = MathF.Cos(mTime) * radius;
            float z = -MathF.Sin(mTime) * radius;

            float aspectRatio = (float)swapchain.Width / (float)swapchain.Height;
            var projection = Perspective(MathF.PI / 4f, aspectRatio, 0.1f, 10f);
            var view = LookAt(new Vector3(x, 0f, z), Vector3.Zero, Vector3.UnitY);
            var model = Matrix4x4.CreateRotationX(mTime / 2f);

            mUniformBuffer!.MapStructure(mPipeline!, nameof(TestShader.u_UniformBuffer), new UniformBufferData
            {
                ModelViewProjection = Matrix4x4.Transpose(projection * view * model)
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
        private float mTime;
    }
}
