using CodePlayground.Graphics.Shaders;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics
{
    public sealed class ImGuiShader
    {
        public struct ProjectionMatrix
        {
            public Matrix4x4<float> Projection;
        }

        public struct VertexIn
        {
            [Layout(Location = 0)]
            public Vector2<float> Position;
            [Layout(Location = 1)]
            public Vector2<float> UV;
            [Layout(Location = 2)]
            public Vector4<float> Color;
        }

        public struct VertexOut
        {
            [OutputPosition]
            public Vector4<float> Position;
            public FragmentIn Data;
        }

        public struct FragmentIn
        {
            [Layout(Location = 0)]
            public Vector2<float> UV;
            [Layout(Location = 1)]
            public Vector4<float> Color;
        }

        [Layout(Set = 0, Binding = 0)]
        public static ProjectionMatrix u_ProjectionBuffer;
        [Layout(Set = 1, Binding = 0)]
        public static Sampler2D<float>? u_Texture;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            return new VertexOut
            {
                Position = u_ProjectionBuffer.Projection * new Vector4<float>(input.Position, 0f, 1f),
                Data = new FragmentIn
                {
                    UV = input.UV,
                    Color = input.Color
                }
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain(FragmentIn input)
        {
            return input.Color * u_Texture!.Sample(input.UV);
        }
    }

    internal struct ImGuiPipelineSpecification : IPipelineSpecification
    {
        public PipelineBlendMode BlendMode => PipelineBlendMode.Default;
        public PipelineFrontFace FrontFace => PipelineFrontFace.Clockwise; // doesn't matter
        public bool EnableDepthTesting => false;
        public bool DisableCulling => true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ImGuiVertex
    {
        public Vector2 Position;
        public Vector2 UV;
        public Vector4 Color;
    }

    internal struct ImGuiBufferData
    {
        public int Size { get; set; }
        public IDeviceBuffer StagingBuffer { get; set; }
        public IDeviceBuffer RenderBuffer { get; set; }
    }

    internal struct ImGuiFrameData
    {
        public Dictionary<DeviceBufferUsage, ImGuiBufferData> Buffers { get; set; }
        public IDisposable Semaphore { get; set; }
    }

    internal struct ImGuiKeyEvent
    {
        public Key Key { get; set; }
        public bool Down { get; set; }
    }

    public sealed class ImGuiController : IDisposable
    {
        private static bool sImGuiInitialized;
        static ImGuiController()
        {
            sImGuiInitialized = false;
        }

        public ImGuiController(IGraphicsContext graphicsContext, IInputContext inputContext, IWindow window, IRenderTarget renderTarget, int frameCount)
        {
            if (sImGuiInitialized)
            {
                throw new InvalidOperationException("An ImGui controller already exists!");
            }

            sImGuiInitialized = true;
            ImGui.CreateContext();

            // todo: viewports
            var io = ImGui.GetIO();
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
            io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;

            mGraphicsContext = graphicsContext;
            mInputContext = inputContext;
            mWindow = window;
            mRenderTarget = renderTarget;

            mFrameData = new ImGuiFrameData[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                mFrameData[i] = new ImGuiFrameData
                {
                    Buffers = new Dictionary<DeviceBufferUsage, ImGuiBufferData>(),
                    Semaphore = mGraphicsContext.CreateSemaphore()
                };
            }

            mPipeline = LoadPipeline();
            mFontAtlas = null;

            string projectionBufferName = nameof(ImGuiShader.u_ProjectionBuffer);
            mProjectionBuffer = CreateUniformBuffer(projectionBufferName);
            mPipeline.Bind(mProjectionBuffer, projectionBufferName, 0);

            mDisposed = false;
            mFrameStarted = false;

            mKeyEvents = new List<ImGuiKeyEvent>();
            mTypedCharacters = new List<char>();
            mNewMousePosition = null;
            mWheelDelta = mWheelPosition = 0f;
            mMouseButtonValues = new Dictionary<int, bool>();
            RegisterInputCallbacks();

            var windowSize = mWindow.Size;
            mWindowWidth = windowSize.X;
            mWindowHeight = windowSize.Y;

            mWindow.FramebufferResize += OnResize;
        }

        ~ImGuiController()
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

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                mGraphicsContext.Device.Wait();

                for (int i = 0; i < mFrameData.Length; i++)
                {
                    var frameData = mFrameData[i];
                    frameData.Semaphore.Dispose();

                    foreach (var buffers in frameData.Buffers.Values)
                    {
                        buffers.StagingBuffer.Dispose();
                        buffers.RenderBuffer.Dispose();
                    }
                }

                mPipeline.Dispose();
                mFontAtlas?.Dispose();
                mProjectionBuffer.Dispose();

                mWindow.FramebufferResize -= OnResize;
            }

            sImGuiInitialized = false;
        }

        private void OnResize(Vector2D<int> newSize)
        {
            mWindowWidth = newSize.X;
            mWindowHeight = newSize.Y;
        }

        private IPipeline LoadPipeline()
        {
            using var compiler = mGraphicsContext.CreateCompiler();
            var transpiler = ShaderTranspiler.Create(compiler.PreferredLanguage);

            var stageSource = transpiler.Transpile<ImGuiShader>();
            var compiledShaders = new Dictionary<ShaderStage, IShader>();

            foreach (var stage in stageSource.Keys)
            {
                var output = stageSource[stage];
                var bytecode = compiler.Compile(output.Source, $"<ImGui shader>:{stage}", transpiler.OutputLanguage, stage, output.Entrypoint);

                var shader = mGraphicsContext.LoadShader(bytecode, stage, output.Entrypoint);
                compiledShaders.Add(stage, shader);
            }

            var pipeline = mGraphicsContext.CreatePipeline(new PipelineDescription
            {
                RenderTarget = mRenderTarget,
                Type = PipelineType.Graphics,
                FrameCount = mFrameData.Length,
                Specification = new ImGuiPipelineSpecification()
            });

            pipeline.Load(compiledShaders);
            foreach (var shader in compiledShaders.Values)
            {
                shader.Dispose();
            }

            return pipeline;
        }

        private IDeviceBuffer CreateUniformBuffer(string resourceName)
        {
            int size = mPipeline.GetBufferSize(resourceName);
            if (size < 0)
            {
                throw new ArgumentException($"Could not find buffer \"{resourceName}\"");
            }

            return mGraphicsContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, size);
        }

        public nint GetTextureID(ITexture texture) => mPipeline.CreateDynamicID(texture, nameof(ImGuiShader.u_Texture), 0);
        public unsafe void LoadFontAtlas(ICommandList commandList)
        {
            var io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height);

            var span = new Span<byte>(pixels, width * height * sizeof(Rgba32));
            var image = Image.LoadPixelData<Rgba32>(span, width, height);

            mFontAtlas = mGraphicsContext.LoadTexture(image, DeviceImageFormat.RGBA8_UNORM, commandList);
            io.Fonts.SetTexID(GetTextureID(mFontAtlas));
        }

        public void NewFrame(double delta)
        {
            if (mFrameStarted)
            {
                ImGui.Render();
            }

            UpdateIO(delta);
            UpdateProjectionBuffer();
            UpdateInput();

            ImGui.NewFrame();
            mFrameStarted = true;
        }

        public void Render(ICommandList commandList, IRenderer renderer, int currentFrame)
        {
            if (!mFrameStarted)
            {
                return;
            }

            ImGui.Render();
            RenderDrawData(ImGui.GetDrawData(), commandList, renderer, currentFrame);

            mFrameStarted = false;
        }

        private unsafe void RenderDrawData(ImDrawDataPtr drawData, ICommandList commandList, IRenderer renderer, int currentFrame)
        {
            if (drawData.CmdListsCount == 0)
            {
                return;
            }

            int totalVertices = drawData.TotalVtxCount;
            int totalIndices = drawData.TotalIdxCount;

            var bufferSizes = new Dictionary<DeviceBufferUsage, int>
            {
                [DeviceBufferUsage.Vertex] = totalVertices * sizeof(ImGuiVertex),
                [DeviceBufferUsage.Index] = totalIndices * sizeof(uint)
            };

            var frameData = mFrameData[currentFrame];
            var obsoleteBuffers = new List<IDeviceBuffer>();

            foreach (var usage in bufferSizes.Keys)
            {
                int requiredSize = bufferSizes[usage];
                int existingSize = 0;

                if (frameData.Buffers.TryGetValue(usage, out ImGuiBufferData bufferData))
                {
                    existingSize = bufferData.Size;
                }

                if (requiredSize > existingSize)
                {
                    if (existingSize > 0)
                    {
                        obsoleteBuffers.Add(bufferData.StagingBuffer);
                        obsoleteBuffers.Add(bufferData.RenderBuffer);
                    }

                    frameData.Buffers[usage] = new ImGuiBufferData
                    {
                        Size = requiredSize,
                        StagingBuffer = mGraphicsContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, requiredSize),
                        RenderBuffer = mGraphicsContext.CreateDeviceBuffer(usage, requiredSize)
                    };
                }
            }

            int vertexOffset = 0;
            int indexOffset = 0;

            for (int i = 0; i < drawData.CmdListsCount; i++)
            {
                var cmdList = drawData.CmdListsRange[i];

                var vertices = new ImGuiVertex[cmdList.VtxBuffer.Size];
                var indices = new uint[cmdList.IdxBuffer.Size];

                for (int j = 0; j < vertices.Length; j++)
                {
                    var vertex = cmdList.VtxBuffer[j];
                    vertices[j] = new ImGuiVertex
                    {
                        Position = vertex.pos,
                        UV = vertex.uv,
                        Color = ImGui.ColorConvertU32ToFloat4(vertex.col)
                    };
                }

                for (int j = 0; j < indices.Length; j++)
                {
                    uint index = cmdList.IdxBuffer[j];
                    indices[j] = index + (uint)vertexOffset;
                }

                frameData.Buffers[DeviceBufferUsage.Vertex].StagingBuffer.CopyFromCPU(vertices, vertexOffset * sizeof(ImGuiVertex));
                frameData.Buffers[DeviceBufferUsage.Index].StagingBuffer.CopyFromCPU(indices, indexOffset * sizeof(uint));

                vertexOffset += vertices.Length;
                indexOffset += indices.Length;
            }

            var device = mGraphicsContext.Device;
            if (obsoleteBuffers.Count > 0)
            {
                device.Wait();
                foreach (var buffer in obsoleteBuffers)
                {
                    buffer.Dispose();
                }
            }

            var transferQueue = device.GetQueue(CommandQueueFlags.Transfer);
            var transferList = transferQueue.Release();
            transferList.Begin();

            foreach (var bufferData in frameData.Buffers.Values)
            {
                bufferData.StagingBuffer.CopyBuffers(transferList, bufferData.RenderBuffer, bufferData.Size);
            }

            transferList.AddSemaphore(frameData.Semaphore, SemaphoreUsage.Signal);
            commandList.AddSemaphore(frameData.Semaphore, SemaphoreUsage.Wait);

            transferList.End();
            transferQueue.Submit(transferList);

            frameData.Buffers[DeviceBufferUsage.Vertex].RenderBuffer.BindVertices(commandList, 0);
            frameData.Buffers[DeviceBufferUsage.Index].RenderBuffer.BindIndices(commandList, DeviceBufferIndexType.UInt32); // converted from ImGui indices (uint16 to uint32)
            mPipeline.Bind(commandList, currentFrame);

            vertexOffset = 0;
            indexOffset = 0;

            for (int i = 0; i < drawData.CmdListsCount; i++)
            {
                var cmdList = drawData.CmdListsRange[i];
                for (int j = 0; j < cmdList.CmdBuffer.Size; j++)
                {
                    var command = cmdList.CmdBuffer[j];
                    mPipeline.Bind(commandList, command.TextureId);

                    renderer.RenderIndexed(commandList, (int)command.IdxOffset + indexOffset, (int)command.ElemCount);
                }

                vertexOffset += cmdList.VtxBuffer.Size;
                indexOffset += cmdList.IdxBuffer.Size;
            }
        }

        private void UpdateIO(double delta)
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(mWindowWidth, mWindowHeight);
            io.DisplayFramebufferScale = Vector2.One;
            io.DeltaTime = (float)delta;
        }

        // https://github.com/g-truc/glm/blob/efec5db081e3aad807d0731e172ac597f6a39447/glm/ext/matrix_clip_space.inl#L16
        // left-handed, 0-1 depth
        // hardcoded for now
        private static Matrix4x4 Orthographic(float left, float right, float bottom, float top, float nearPlane, float farPlane)
        {
            return new Matrix4x4(2f / (right - left), 0f, 0f, -(right + left) / (right - left),
                                 0f, 2f / (top - bottom), 0f, -(top + bottom) / (top - bottom),
                                 0f, 0f, 1f / (farPlane - nearPlane), -nearPlane / (farPlane - nearPlane),
                                 0f, 0f, 0f, 1f);
        }

        private void UpdateProjectionBuffer()
        {
            var projection = Orthographic(0f, mWindowWidth, mWindowHeight, 0f, -1f, 1f);
            mProjectionBuffer.CopyFromCPU(Matrix4x4.Transpose(projection));
        }

        private void UpdateInput()
        {
            var io = ImGui.GetIO();
            io.AddMouseWheelEvent(0f, mWheelDelta);

            if (mNewMousePosition is not null)
            {
                var position = mNewMousePosition.Value;
                io.AddMousePosEvent(position.X, position.Y);
            }

            foreach (int button in mMouseButtonValues.Keys)
            {
                io.AddMouseButtonEvent(button, mMouseButtonValues[button]);
            }

            foreach (char character in mTypedCharacters)
            {
                io.AddInputCharacter(character);
            }

            foreach (var keyEvent in mKeyEvents)
            {
                var key = keyEvent.Key switch
                {
                    Key.ShiftLeft or Key.ShiftRight => ImGuiKey.ModShift,
                    Key.ControlLeft or Key.ControlRight => ImGuiKey.ModCtrl,
                    Key.AltLeft or Key.AltRight => ImGuiKey.ModAlt,
                    Key.SuperLeft or Key.SuperRight => ImGuiKey.ModSuper,
                    Key.Up => ImGuiKey.UpArrow,
                    Key.Down => ImGuiKey.DownArrow,
                    Key.Right => ImGuiKey.RightArrow,
                    Key.Left => ImGuiKey.LeftArrow,
                    Key.Number0 => ImGuiKey._0,
                    Key.Number1 => ImGuiKey._1,
                    Key.Number2 => ImGuiKey._2,
                    Key.Number3 => ImGuiKey._3,
                    Key.Number4 => ImGuiKey._4,
                    Key.Number5 => ImGuiKey._5,
                    Key.Number6 => ImGuiKey._6,
                    Key.Number7 => ImGuiKey._7,
                    Key.Number8 => ImGuiKey._8,
                    Key.Number9 => ImGuiKey._9,
                    _ => Enum.Parse<ImGuiKey>(keyEvent.Key.ToString(), true)
                };

                io.AddKeyEvent(key, keyEvent.Down);
            }

            mKeyEvents.Clear();
            mTypedCharacters.Clear();
            mNewMousePosition = null;
            mWheelPosition += mWheelDelta;
            mWheelDelta = 0f;
            mMouseButtonValues.Clear();
        }

        private void RegisterInputCallbacks()
        {
            foreach (var mouse in mInputContext.Mice)
            {
                mouse.MouseMove += (mouse, delta) => mNewMousePosition = mouse.Position;
                mouse.MouseDown += (mouse, button) => mMouseButtonValues[(int)button] = true;
                mouse.MouseUp += (mouse, button) => mMouseButtonValues[(int)button] = false;
                mouse.Scroll += (mouse, wheel) => mWheelDelta = wheel.Y - mWheelPosition;
            }

            foreach (var keyboard in mInputContext.Keyboards)
            {
                keyboard.KeyChar += (keyboard, character) => mTypedCharacters.Add(character);

                keyboard.KeyDown += (keyboard, key, scancode) => mKeyEvents.Add(new ImGuiKeyEvent
                {
                    Key = key,
                    Down = true
                });

                keyboard.KeyUp += (keyboard, key, scancode) => mKeyEvents.Add(new ImGuiKeyEvent
                {
                    Key = key,
                    Down = false
                });
            }
        }

        private readonly IGraphicsContext mGraphicsContext;
        private readonly IInputContext mInputContext;
        private readonly IWindow mWindow;
        private readonly IRenderTarget mRenderTarget;

        private ImGuiFrameData[] mFrameData;
        private readonly IPipeline mPipeline;
        private readonly IDeviceBuffer mProjectionBuffer;
        private ITexture? mFontAtlas;

        private bool mDisposed, mFrameStarted;
        private int mWindowWidth, mWindowHeight;

        private readonly List<ImGuiKeyEvent> mKeyEvents;
        private readonly List<char> mTypedCharacters;
        private Vector2? mNewMousePosition;
        private float mWheelDelta, mWheelPosition;
        private readonly Dictionary<int, bool> mMouseButtonValues;
    }
}