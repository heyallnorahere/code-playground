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
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ChessAI.GUI
{
    internal struct SamplerSettings : ISamplerSettings
    {
        public AddressMode AddressMode { get; set; }

        public SamplerFilter Filter { get; set; }
    }

    internal enum AIType
    {
        Player,
        NeuralNetwork
    }

    public sealed class ChessController : IDisposable
    {
        public const float TileWidth = 1f / Board.Width;

        public ChessController(IInputContext inputContext, IWindow window, IGraphicsContext graphicsContext, Network network)
        {
            using var constructorEvent = OptickMacros.Event();
            mDisposed = false;

            mContext = graphicsContext;
            mWindow = window;

            mFEN = string.Empty;
            mColor = PlayerColor.White;

            mAIType = AIType.Player;
            mNetwork = network;
            mInvalidMove = false;

            mPromotionFile = -1;
            mEngine = new Engine
            {
                Board = mBoard = Board.Create()
            };

            mEngine.Check += color => Console.WriteLine($"Check! {color}'s king is in check!");
            mEngine.Checkmate += color => Console.WriteLine($"Checkmate! {color} has lost!");

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
                    var color = (x + y) % 2 == 0 ? Vector3.One : new Vector3(0f, 0.5f, 0f);
                    gridData[rowOffset + x] = new Rgba32
                    {
                        R = (byte)float.Round(color.X * byte.MaxValue),
                        G = (byte)float.Round(color.Y * byte.MaxValue),
                        B = (byte)float.Round(color.Z * byte.MaxValue),
                        A = byte.MaxValue
                    };
                }
            }

            const int textureCount = 12; // 6 white pieces, 6 black pieces
            var pieceBuffers = new IDeviceBuffer[textureCount];
            mPieceTextures = new ITexture[textureCount];

            var pieceTypes = Enum.GetValues<PieceType>();
            var playerColors = Enum.GetValues<PlayerColor>();
            var assembly = GetType().Assembly;

            foreach (var type in pieceTypes)
            {
                if (type is PieceType.None)
                {
                    continue;
                }

                foreach (var color in playerColors)
                {
                    int index = GetPieceTextureIndex(new PieceInfo
                    {
                        Color = color,
                        Type = type
                    });

                    string resourceName = $"ChessAI.Resources.Pieces.{color}_{type}.png";
                    var stream = assembly.GetManifestResourceStream(resourceName);

                    if (stream is null)
                    {
                        throw new FileNotFoundException("Failed to find piece texture!");
                    }

                    var image = Image.Load<Rgba32>(stream);
                    var imageData = new Rgba32[image.Width * image.Height];

                    int imageBufferSize = imageData.Length * Marshal.SizeOf<Rgba32>();
                    var imageBuffer = pieceBuffers[index] = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, imageBufferSize);

                    image.CopyPixelDataTo(imageData);
                    imageBuffer.CopyFromCPU(imageData);

                    mPieceTextures[index] = mContext.CreateDeviceImage(new DeviceImageInfo
                    {
                        Size = image.Size,
                        Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                        Format = DeviceImageFormat.RGBA8_UNORM
                    }).CreateTexture(true);
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
                var gridImage = mGridTexture.Image;
                var layout = gridImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

                gridImage.TransitionLayout(commandList, gridImage.Layout, layout);
                gridImage.CopyFromBuffer(commandList, stagingBuffer, layout);

                gridImage.Layout = layout;
                for (int i = 0; i < textureCount; i++)
                {
                    var pieceStagingBuffer = pieceBuffers[i];
                    commandList.PushStagingObject(pieceStagingBuffer);

                    var pieceImage = mPieceTextures[i].Image;
                    pieceImage.TransitionLayout(commandList, pieceImage.Layout, layout);
                    pieceImage.CopyFromBuffer(commandList, pieceStagingBuffer, layout);

                    pieceImage.Layout = layout;
                }
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

                foreach (var texture in mPieceTextures)
                {
                    texture.Dispose();
                }
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

                if (ImGui.BeginCombo("AI type", mAIType.ToString()))
                {
                    var aiTypes = Enum.GetValues<AIType>();
                    foreach (var type in aiTypes)
                    {
                        bool isSelected = type == mAIType;
                        if (ImGui.Selectable(type.ToString(), isSelected))
                        {
                            mAIType = type;
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

            using (OptickMacros.Category("Mouse events", Category.Input))
            {
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
                        var mouseBoardPosition = clickPosition / TileWidth;

                        int x = (int)float.Floor(mouseBoardPosition.X);
                        int y = (int)float.Floor(mouseBoardPosition.Y);
                        var boardPosition = new Coord(x, (mColor != PlayerColor.White) ? y : (Board.Width - (y + 1)));

                        if (mMouseDown && mPromotionFile < 0 && mBoard.GetPiece(boardPosition, out _))
                        {
                            mDraggedPiece = boardPosition;
                        }

                        bool canMove = mBoard.CurrentTurn == mColor || mInvalidMove || mAIType == AIType.Player;
                        if (mMouseUp && canMove)
                        {
                            if (mDraggedPiece is not null)
                            {
                                var piecePosition = mDraggedPiece.Value;
                                if (mBoard.GetPiece(piecePosition, out PieceInfo draggedPiece) && draggedPiece.Color == mBoard.CurrentTurn)
                                {
                                    var move = new Move
                                    {
                                        Position = piecePosition,
                                        Destination = boardPosition
                                    };

                                    if (mEngine.CommitMove(move))
                                    {
                                        mInvalidMove = false;
                                        if (mEngine.ShouldPromote(mColor, true))
                                        {
                                            mPromotionFile = x;
                                        }
                                    }
                                }
                            }
                            else if (mPromotionFile == x && y < 4)
                            {
                                var pieceType = (PieceType)((int)PieceType.Queen + y); // hacky
                                mEngine.Promote(pieceType);

                                mPromotionFile = -1;
                            }
                        }
                    }

                    if (mMouseUp)
                    {
                        mDraggedPiece = null;
                    }
                }

                mMouseDown = false;
                mMouseUp = false;
            }

            using (OptickMacros.Event("AI move"))
            {
                if (mAIType == AIType.NeuralNetwork)
                {
                    mInvalidMove = true; // not implemented
                }
            }
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
            int min = int.Min(size.X, size.Y);
            int max = int.Max(size.X, size.Y);
            float aspectRatio = (float)max / min;

            var ratioVector = Vector2.One;
            if (size.X > size.Y)
            {
                ratioVector.X = aspectRatio;
            }
            else
            {
                ratioVector.Y = aspectRatio;
            }

            var math = new MatrixMath(mContext);
            var viewProjection = math.Orthographic(0f, ratioVector.X, 0f, ratioVector.Y, -1f, 1f);
            renderer.BeginScene(viewProjection);

            var renderOffset = (ratioVector - Vector2.One) / 2f;
            renderer.Submit(new RenderedQuad
            {
                Position = renderOffset + Vector2.One * 0.5f,
                Size = Vector2.One,
                RotationRadians = 0f,
                Color = Vector4.One,
                Texture = mGridTexture
            });

            if (mPromotionFile >= 0)
            {
                renderer.Submit(new RenderedQuad
                {
                    Position = renderOffset + new Vector2(mPromotionFile + 0.5f, 6f) * TileWidth,
                    Size = new Vector2(1f, 4f) * TileWidth,
                    RotationRadians = 0f,
                    Color = Vector4.One,
                    Texture = null
                });
            }

            using (OptickMacros.Event("Render pieces"))
            {
                IRenderedShape? draggedPiece = null;
                for (int y = 0; y < Board.Width; y++)
                {
                    for (int x = 0; x < Board.Width; x++)
                    {
                        var boardPosition = new Coord(x, mColor != PlayerColor.White ? (Board.Width - y - 1) : y);

                        ITexture? pieceTexture = null;
                        if (x == mPromotionFile && y >= Board.Width - 4)
                        {
                            int pieceOffset = Board.Width - (y + 1);
                            var pieceType = (PieceType)((int)PieceType.Queen + pieceOffset);

                            pieceTexture = GetPieceTexture(new PieceInfo
                            {
                                Color = mColor,
                                Type = pieceType
                            });
                        }
                        else if (mBoard.GetPiece(boardPosition, out PieceInfo piece))
                        {
                            pieceTexture = GetPieceTexture(piece);
                        }

                        if (pieceTexture is not null)
                        {
                            Vector2 position;
                            bool dragged;

                            if (boardPosition != mDraggedPiece)
                            {
                                position = new Vector2(x + 0.5f, y + 0.5f) * TileWidth + renderOffset;
                                dragged = false;
                            }
                            else
                            {
                                position = new Vector2
                                {
                                    X = mMousePosition.X / min,
                                    Y = 1f - mMousePosition.Y / min
                                };

                                dragged = true;
                            }

                            var shape = new RenderedQuad
                            {
                                Position = position,
                                Size = Vector2.One * TileWidth * 0.9f,
                                RotationRadians = 0f,
                                Color = Vector4.One,
                                Texture = pieceTexture
                            };

                            if (dragged)
                            {
                                draggedPiece = shape;
                            }
                            else
                            {
                                renderer.Submit(shape);
                            }
                        }
                    }
                }

                if (draggedPiece is not null)
                {
                    renderer.Submit(draggedPiece);
                }
            }

            renderer.EndScene();
        }

        private void LoadFEN(string fen)
        {
            mBoard.Dispose();
            mEngine.Board = mBoard = Board.Create(fen) ?? throw new ArgumentException("Invalid FEN string!");

            mDraggedPiece = null;
            mPromotionFile = -1;
            mInvalidMove = false;
        }

        private static int GetPieceTextureIndex(PieceInfo piece)
        {
            int index = (int)piece.Type - 1;
            if (piece.Color != PlayerColor.White && piece.Type != PieceType.None)
            {
                index += 6; // total number of pieces
            }

            return index;
        }

        private ITexture GetPieceTexture(PieceInfo piece)
        {
            int index = GetPieceTextureIndex(piece);
            return mPieceTextures[index];
        }

        private readonly IGraphicsContext mContext;
        private readonly IWindow mWindow;

        private Vector2 mMousePosition;
        private bool mMouseDown, mMouseUp;
        private Coord? mDraggedPiece;
        private int mPromotionFile;

        private AIType mAIType;
        private readonly Network mNetwork;
        private bool mInvalidMove;

        private string mFEN;
        private PlayerColor mColor;
        private Board mBoard;
        private readonly Engine mEngine;

        private readonly ITexture[] mPieceTextures;
        private readonly ITexture mGridTexture;
        private readonly IDisposable mSemaphore;
        private bool mUseSemaphore;

        private bool mDisposed;
    }
}
