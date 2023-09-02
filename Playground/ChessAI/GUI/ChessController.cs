using CodePlayground.Graphics;
using ImGuiNET;
using LibChess;
using MachineLearning;
using Optick.NET;
using Silk.NET.Input;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ChessAI.GUI
{
    internal struct SamplerSettings : ISamplerSettings
    {
        public AddressMode AddressMode { get; set; }

        public SamplerFilter Filter { get; set; }
    }

    public sealed class ChessController : IDisposable
    {
        public const float TileWidth = 1f / Board.Width;

        public ChessController(IInputContext inputContext, IWindow window, IGraphicsContext graphicsContext, Network mNetwork)
        {
            using var constructorEvent = OptickMacros.Event();
            mDisposed = false;

            mContext = graphicsContext;
            mWindow = window;

            mFEN = string.Empty;
            mColor = PlayerColor.White;

            mEngine = new Engine
            {
                Board = mBoard = Board.Create()
            };

            foreach (var mouse in inputContext.Mice)
            {
                mouse.MouseMove += (mouse, position) => mMousePosition = position;
                mouse.MouseDown += (mouse, button) =>
                {
                    if (button != MouseButton.Left)
                    {
                        return;
                    }

                    mMouseDown = true;
                };

                mouse.MouseUp += (mouse, button) =>
                {
                    if (button != MouseButton.Left)
                    {
                        return;
                    }

                    mMouseUp = true;
                };
            }

            mUseSemaphore = true;
            mSemaphore = mContext.CreateSemaphore();

            mGridTexture = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(Board.Width),
                Usage = DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            }).CreateTexture(true, new SamplerSettings
            {
                AddressMode = AddressMode.Repeat,
                Filter = SamplerFilter.Nearest
            });

            var gridData = new Rgba32[Board.Width * Board.Width];
            for (int y = 0; y < Board.Width; y++)
            {
                int rowOffset = y * Board.Width;
                for (int x = 0; x < Board.Width; x++)
                {
                    var color = (x + y) % 2 != 0 ? Vector3.One : new Vector3(0f, 0.5f, 0f);
                    gridData[rowOffset + x] = new Rgba32
                    {
                        R = (byte)float.Round(color.X * byte.MaxValue),
                        G = (byte)float.Round(color.Y * byte.MaxValue),
                        B = (byte)float.Round(color.Z * byte.MaxValue),
                        A = byte.MaxValue
                    };
                }
            }

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            int bufferSize = gridData.Length * Marshal.SizeOf<Rgba32>();
            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);

            stagingBuffer.CopyFromCPU(gridData);
            commandList.PushStagingObject(stagingBuffer);

            commandList.Begin();
            using (commandList.Context(GPUQueueType.Transfer))
            {
                var image = mGridTexture.Image;
                var layout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

                image.TransitionLayout(commandList, image.Layout, layout);
                image.CopyFromBuffer(commandList, stagingBuffer, layout);

                image.Layout = layout;
            }

            commandList.AddSemaphore(mSemaphore, SemaphoreUsage.Signal);
            commandList.End();
            queue.Submit(commandList);
        }

        ~ChessController()
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
            using var disposeEvent = OptickMacros.Event();
            if (disposing)
            {
                mBoard.Dispose();
                mEngine.Dispose();

                mGridTexture.Dispose();
                mSemaphore.Dispose();
            }
        }

        public void Update()
        {
            using var updateEvent = OptickMacros.Event();
            using (OptickMacros.Event("Chess options"))
            {
                ImGui.Begin("Chess options");
                if (ImGui.BeginCombo("Player color", mColor.ToString()))
                {
                    var colors = Enum.GetValues<PlayerColor>();
                    foreach (var color in colors)
                    {
                        bool isSelected = color == mColor;
                        if (ImGui.Selectable(color.ToString(), isSelected))
                        {
                            mColor = color;
                        }

                        if (isSelected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }

                    ImGui.EndCombo();
                }

                ImGui.InputTextWithHint("##fen", Pond.DefaultFEN, ref mFEN, 512);
                ImGui.SameLine();

                if (ImGui.Button("Load FEN"))
                {
                    LoadFEN(string.IsNullOrEmpty(mFEN) ? Pond.DefaultFEN : mFEN);
                }

                ImGui.End();
            }

            if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow | ImGuiHoveredFlags.AllowWhenBlockedByPopup) && !ImGui.IsAnyItemHovered())
            {
                var size = mWindow.FramebufferSize;
                int min = int.Min(size.X, size.Y);
                int max = int.Max(size.X, size.Y);

                float aspectRatio = (float)max / min;
                float offset = (aspectRatio - 1f) / 2f;

                var clickOffset = Vector2.Zero;
                if (size.X > size.Y)
                {
                    clickOffset.X = offset;
                }
                else
                {
                    clickOffset.Y = offset;
                }

                var clickPosition = mMousePosition / min - clickOffset;
                if (clickPosition.X >= 0f && clickPosition.X <= 1f &&
                    clickPosition.Y >= 0f && clickPosition.Y <= 1f)
                {
                    if (mMouseDown)
                    {
                        Console.WriteLine($"Mouse clicked at {clickPosition}");
                    }

                    if (mMouseUp)
                    {
                        Console.WriteLine($"Mouse released at {clickPosition}");
                    }
                }
            }

            mMouseDown = false;
            mMouseUp = false;
        }

        public void Render(BatchRenderer renderer)
        {
            using var renderEvent = OptickMacros.Event();
            if (mUseSemaphore)
            {
                var commandList = renderer.ComamndList;
                if (commandList is not null)
                {
                    commandList.AddSemaphore(mSemaphore, SemaphoreUsage.Wait);
                    mUseSemaphore = false;
                }
            }

            var size = mWindow.FramebufferSize;
            float aspectRatio = (float)size.X / size.Y;

            var ratioVector = Vector2.One;
            if (aspectRatio > 1f)
            {
                ratioVector.X = aspectRatio;
            }
            else
            {
                ratioVector.Y = 1f / aspectRatio;
            }

            var math = new MatrixMath(mContext);
            var viewProjection = math.Orthographic(0f, ratioVector.X, 0f, ratioVector.Y, -1f, 1f);
            renderer.BeginScene(viewProjection);

            var renderOffset = (ratioVector - Vector2.One) / 2f;
            renderer.Submit(new RenderedQuad
            {
                Position = renderOffset + Vector2.One * 0.5f,
                Size = Vector2.One,
                RotationDegrees = 0f,
                Color = Vector4.One,
                Texture = mGridTexture
            });

            renderer.EndScene();
        }

        private void LoadFEN(string fen)
        {
            mBoard.Dispose();
            mEngine.Board = mBoard = Board.Create(fen) ?? throw new ArgumentException("Invalid FEN string!");
        }

        private readonly IGraphicsContext mContext;
        private readonly IWindow mWindow;
        private Vector2 mMousePosition;
        private bool mMouseDown, mMouseUp;

        private string mFEN;
        private PlayerColor mColor;
        private Board mBoard;
        private readonly Engine mEngine;

        private readonly ITexture mGridTexture;
        private readonly IDisposable mSemaphore;
        private bool mUseSemaphore;

        private bool mDisposed;
    }
}
