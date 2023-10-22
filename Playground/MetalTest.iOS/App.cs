using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;
using System.Numerics;

namespace MetalTest.iOS
{
    public struct TestShaderPushConstants
    {
        public Vector2<float> Offset;
        public Vector4<float> Color;
    }

    internal struct PushConstants
    {
        public Vector2 Offset;
        public Vector4 Color;
    }

    [CompiledShader]
    public sealed class TestShader
    {
        [Layout(PushConstant = true)]
        public static TestShaderPushConstants u_PushConstants;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        [return: ShaderVariable(ShaderVariableID.OutputPosition)]
        public static Vector4<float> VertexMain([Layout(Location = 0)] Vector2<float> position)
        {
            return new Vector4<float>(position + u_PushConstants.Offset, 0f, 1f);
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain()
        {
            return u_PushConstants.Color;
        }
    }

    internal struct PipelineSpecification : IPipelineSpecification
    {
        public PipelineBlendMode BlendMode { get; set; }
        public PipelineFrontFace FrontFace { get; set; }
        public bool EnableDepthTesting { get; set; }
        public bool DisableCulling { get; set; }
    }

    [ApplicationTitle("MetalTest")]
    internal sealed class App : GraphicsApplication
    {
        private static readonly Vector2[] sVertices;
        private static readonly uint[] sIndices;

        static App()
        {
            const float size = 0.05f;
            sVertices = new Vector2[]
            {
                new Vector2(0f, size / 2f),
                new Vector2(size / -2f, size / -2f),
                new Vector2(size / 2f, size / -2f)
            };

            sIndices = new uint[]
            {
                0, 1, 2
            };
        }

        public static int Main(string[] args) => RunApplication<App>(args);

        public App()
        {
            Load += OnLoad;
            Closing += OnClose;
            Render += OnRender;
        }

        private unsafe void OnLoad()
        {
            var context = CreateGraphicsContext();
            var swapchain = context.Swapchain;

            using var vertexStaging = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, sVertices.Length * sizeof(Vector2));
            using var indexStaging = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, sIndices.Length * sizeof(uint));

            vertexStaging.CopyFromCPU(sVertices);
            indexStaging.CopyFromCPU(sIndices);

            mVertices = context.CreateDeviceBuffer(DeviceBufferUsage.Vertex, vertexStaging.Size);
            mIndices = context.CreateDeviceBuffer(DeviceBufferUsage.Index, indexStaging.Size);
            mRenderer = context.CreateRenderer();

            using var library = new ShaderLibrary(context, GetType().Assembly);
            mPipeline = library.LoadPipeline<TestShader>(new PipelineDescription
            {
                RenderTarget = swapchain?.RenderTarget,
                Type = PipelineType.Graphics,
                FrameCount = swapchain?.FrameCount ?? SynchronizationFrames,
                Specification = new PipelineSpecification
                {
                    BlendMode = PipelineBlendMode.None,
                    FrontFace = PipelineFrontFace.CounterClockwise,
                    EnableDepthTesting = false,
                    DisableCulling = false
                }
            });

            var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            vertexStaging.CopyBuffers(commandList, mVertices, vertexStaging.Size);
            indexStaging.CopyBuffers(commandList, mIndices, indexStaging.Size);

            commandList.End();
            queue.Submit(commandList, true);
        }

        private void OnRender(FrameRenderInfo renderInfo)
        {
            var commandList = renderInfo.CommandList!;
            renderInfo.RenderTarget!.BeginRender(commandList, renderInfo.Framebuffer!, new Vector4(0f, 0f, 0f, 1f));

            mPipeline?.Bind(commandList, renderInfo.CurrentImage);
            mPipeline?.PushConstants(commandList, data =>
            {
                mPipeline.ReflectionView.MapStructure(data, nameof(TestShader.u_PushConstants), new PushConstants
                {
                    Offset = Vector2.Zero,
                    Color = new Vector4(1f, 0f, 0f, 1f)
                });
            });

            mVertices?.BindVertices(commandList, 0);
            mIndices?.BindIndices(commandList, DeviceBufferIndexType.UInt32);
            mRenderer?.RenderIndexed(commandList, 0, sIndices.Length);

            renderInfo.RenderTarget!.EndRender(commandList);
        }

        private void OnClose()
        {
            mVertices?.Dispose();
            mIndices?.Dispose();
            mPipeline?.Dispose();

            GraphicsContext?.Dispose();
        }

        private IDeviceBuffer? mVertices, mIndices;
        private IPipeline? mPipeline;
        private IRenderer? mRenderer;
    }
}