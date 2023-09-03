using ChessAI.Shaders;
using CodePlayground.Graphics;
using Optick.NET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ChessAI.GUI
{
    public struct RendererStats
    {
        public int DrawCalls;
        public int QuadCount;
        public int TexturesPushed;

        public int VertexCount;
        public int IndexCount;

        public void Reset()
        {
            DrawCalls = 0;
            QuadCount = 0;
            TexturesPushed = 0;

            VertexCount = 0;
            IndexCount = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RendererVertex
    {
        public Vector2 Position;
        public Vector4 Color;
        public Vector2 UV;

        public int TextureIndex;
    }

    public interface IRenderedShape
    {
        public void GetVertices(PipelineFrontFace frontFace, BatchRenderer renderer, IList<RendererVertex> vertices, IList<uint> indices, out int quadCount);
    }

    internal struct RenderBatch
    {
        public List<IRenderedShape> Shapes;
        public List<ITexture> Textures;
        public Type Shader;
    }

    internal struct SceneUsedPipelineData
    {
        public IPipeline Pipeline;
        public Type ShaderType;
    }

    internal sealed class RendererVertexData
    {
        public RendererVertexData()
        {
            mVertices = new List<IDeviceBuffer>();
            mIndices = new List<IDeviceBuffer>();
            mStaging = new List<IDeviceBuffer>();
        }

        public void MergeInto(RendererVertexData destination)
        {
            MergeInto(destination, DeviceBufferUsage.Vertex);
            MergeInto(destination, DeviceBufferUsage.Index);
            MergeInto(destination, DeviceBufferUsage.Staging);
        }

        private void MergeInto(RendererVertexData destination, DeviceBufferUsage bufferType)
        {
            var sourceList = GetList(bufferType);
            var destinationList = destination.GetList(bufferType);

            destinationList.AddRange(sourceList);
            destinationList.Sort(Comparison);
        }

        public void PushBuffer(DeviceBufferUsage bufferType, IDeviceBuffer buffer)
        {
            var list = GetList(bufferType);
            list.Add(buffer);
            list.Sort(Comparison);
        }

        public IDeviceBuffer? PopBuffer(DeviceBufferUsage bufferType, int requiredSize)
        {
            var list = GetList(bufferType);

            int index = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Size >= requiredSize)
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                var buffer = list[index];

                list.RemoveAt(index);
                return buffer;
            }

            return null;
        }

        public void Clear(bool dispose)
        {
            Clear(DeviceBufferUsage.Vertex, dispose);
            Clear(DeviceBufferUsage.Index, dispose);
            Clear(DeviceBufferUsage.Staging, dispose);
        }

        private void Clear(DeviceBufferUsage bufferType, bool dispose)
        {
            var list = GetList(bufferType);
            if (dispose)
            {
                foreach (var buffer in list)
                {
                    buffer.Dispose();
                }
            }

            list.Clear();
        }

        private List<IDeviceBuffer> GetList(DeviceBufferUsage bufferType) => bufferType switch
        {
            DeviceBufferUsage.Vertex => mVertices,
            DeviceBufferUsage.Index => mIndices,
            DeviceBufferUsage.Staging => mStaging,
            _ => throw new ArgumentException("Invalid buffer usage!")
        };

        private readonly List<IDeviceBuffer> mVertices, mIndices, mStaging;

        private static int Comparison(IDeviceBuffer lhs, IDeviceBuffer rhs) => lhs.Size.CompareTo(rhs.Size);
    }

    internal struct RenderingScene
    {
        public RenderBatch CurrentBatch;
        public Dictionary<ulong, List<SceneUsedPipelineData>> UsedPipelines;
        public RendererVertexData VertexData;
        public bool Active;
    }

    internal struct CameraData
    {
        public BufferMatrix ViewProjection;
    }

    internal struct UsedPipelineData
    {
        public List<IPipeline> CurrentlyUsing;
        public Queue<IPipeline> Used;
    }

    internal struct RenderTargetPipelineData
    {
        public Dictionary<Type, UsedPipelineData> Data;
    }

    internal struct RendererFrameData
    {
        public Dictionary<ulong, RenderTargetPipelineData> Pipelines;
        public RendererVertexData VertexData;
    }

    internal struct RenderTargetData
    {
        public IRenderTarget RenderTarget;
        public IFramebuffer Framebuffer;
        public Vector4 ClearColor;
        public bool Active;
    }

    internal readonly struct RendererPipelineSpecification : IPipelineSpecification
    {
        public RendererPipelineSpecification(bool viewportFlipped)
        {
            mViewportFlipped = viewportFlipped;
        }

        public PipelineBlendMode BlendMode => PipelineBlendMode.Default;
        public PipelineFrontFace FrontFace => BatchRenderer.FrontFace;

        public bool EnableDepthTesting => false;
        public bool DisableCulling => false;

        private readonly bool mViewportFlipped;
    }

    // based off of
    // https://github.com/yodasoda1219/sge/blob/main/sge/src/sge/renderer/renderer.h
    public sealed class BatchRenderer : IDisposable
    {
        public const PipelineFrontFace FrontFace = PipelineFrontFace.Clockwise; // for consistency

        public BatchRenderer(IGraphicsContext context, IRenderer renderer, IFrameSynchronizationManager synchronizationManager)
        {
            using var constructorEvent = OptickMacros.Event();
            mDisposed = false;

            mContext = context;
            mRenderer = renderer;
            mSynchronizationManager = synchronizationManager;

            mSignaledSemaphores = new List<IDisposable>();
            mUsedSemaphores = new Queue<IDisposable>();

            mCurrentScene = new RenderingScene
            {
                CurrentBatch = new RenderBatch
                {
                    Shapes = new List<IRenderedShape>(),
                    Textures = new List<ITexture>()
                },
                UsedPipelines = new Dictionary<ulong, List<SceneUsedPipelineData>>(),
                VertexData = new RendererVertexData(),
                Active = false
            };

            mFrameData = new RendererFrameData[mSynchronizationManager.FrameCount];
            mRenderTargets = new Stack<RenderTargetData>();
            mCommandList = null;

            for (int i = 0; i < mFrameData.Length; i++)
            {
                mFrameData[i] = new RendererFrameData
                {
                    Pipelines = new Dictionary<ulong, RenderTargetPipelineData>(),
                    VertexData = new RendererVertexData()
                };
            }

            mLibrary = new ShaderLibrary(mContext, GetType().Assembly);
            mCameraBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, Marshal.SizeOf<CameraData>());

            mWhiteTexture = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new SixLabors.ImageSharp.Size(1, 1),
                Usage = DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            }).CreateTexture(true);

            var imageData = new byte[] { 255, 255, 255, 255 };
            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, imageData.Length * Marshal.SizeOf<byte>());
            stagingBuffer.CopyFromCPU(imageData);

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            commandList.PushStagingObject(stagingBuffer);
            SignalSemaphore(commandList);

            using (commandList.Context(GPUQueueType.Transfer))
            {
                var image = mWhiteTexture.Image;
                var layout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

                image.TransitionLayout(commandList, image.Layout, layout);
                image.CopyFromBuffer(commandList, stagingBuffer, layout);

                image.Layout = layout;
            }

            commandList.End();
            queue.Submit(commandList);
        }

        ~BatchRenderer()
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
            if (mCurrentScene.Active)
            {
                throw new InvalidOperationException("A scene is currently rendering!");
            }

            if (mRenderTargets.Count > 0)
            {
                throw new InvalidOperationException("Not all render passes have been popped!");
            }

            if (disposing)
            {
                foreach (var frameRenderData in mFrameData)
                {
                    frameRenderData.VertexData.Clear(true);
                    foreach (var renderTargetPipelines in frameRenderData.Pipelines.Values)
                    {
                        foreach (var pipelines in renderTargetPipelines.Data.Values)
                        {
                            foreach (var pipeline in pipelines.CurrentlyUsing)
                            {
                                pipeline.Dispose();
                            }

                            while (pipelines.Used.TryDequeue(out IPipeline? pipeline))
                            {
                                pipeline.Dispose();
                            }
                        }
                    }
                }

                mLibrary.Dispose();
                mWhiteTexture.Dispose();
                mCameraBuffer.Dispose();

                foreach (var semaphore in mSignaledSemaphores)
                {
                    semaphore.Dispose();
                }

                while (mUsedSemaphores.TryDequeue(out IDisposable? semaphore))
                {
                    semaphore.Dispose();
                }
            }
        }

        public void BeginFrame(ICommandList commandList)
        {
            using var beginFrameEvent = OptickMacros.Event();
            if (mCommandList is not null)
            {
                throw new InvalidOperationException("A frame is already active!");
            }

            mCommandList = commandList;
            mStats.Reset();

            var frameData = mFrameData[mSynchronizationManager.CurrentFrame];
            foreach (var pipelines in frameData.Pipelines.Values)
            {
                foreach (var data in pipelines.Data.Values)
                {
                    foreach (var pipeline in data.CurrentlyUsing)
                    {
                        data.Used.Enqueue(pipeline);
                    }

                    data.CurrentlyUsing.Clear();
                }
            }
        }

        public void EndFrame()
        {
            using var endFrameEvent = OptickMacros.Event();
            if (mCommandList is null)
            {
                throw new InvalidOperationException("No frame is active!");
            }

            if (mRenderTargets.Count > 0)
            {
                throw new InvalidOperationException("Not all render passes have been popped!");
            }

            foreach (var semaphore in mSignaledSemaphores)
            {
                mCommandList.AddSemaphore(semaphore, SemaphoreUsage.Wait);
                mUsedSemaphores.Enqueue(semaphore);
            }

            mSignaledSemaphores.Clear();
            mCommandList = null;
        }

        public void BeginScene(Matrix4x4 viewProjection)
        {
            using var beginSceneEvent = OptickMacros.Event();
            if (mCurrentScene.Active)
            {
                throw new InvalidOperationException("A scene is already active!");
            }

            var math = new MatrixMath(mContext);
            mCameraBuffer.CopyFromCPU(new CameraData
            {
                ViewProjection = math.TranslateMatrix(viewProjection)
            });

            mCurrentScene.UsedPipelines.Clear();
            mCurrentScene.VertexData.Clear(false);
            mCurrentScene.Active = true;

            BeginBatch();
        }

        public void EndScene()
        {
            using var endSceneEvent = OptickMacros.Event();
            if (!mCurrentScene.Active)
            {
                throw new InvalidOperationException("No scene is active!");
            }

            FlushBatch();

            int currentFrame = mSynchronizationManager.CurrentFrame;
            var frameData = mFrameData[currentFrame];
            mCurrentScene.VertexData.MergeInto(frameData.VertexData);

            foreach (var renderTarget in mCurrentScene.UsedPipelines.Keys)
            {
                var pipelines = mCurrentScene.UsedPipelines[renderTarget];
                if (!frameData.Pipelines.TryGetValue(renderTarget, out RenderTargetPipelineData framePipelineData))
                {
                    framePipelineData = frameData.Pipelines[renderTarget] = new RenderTargetPipelineData
                    {
                        Data = new Dictionary<Type, UsedPipelineData>()
                    };
                }

                foreach (var pipeline in pipelines)
                {
                    if (!framePipelineData.Data.TryGetValue(pipeline.ShaderType, out UsedPipelineData framePipelines))
                    {
                        framePipelines = framePipelineData.Data[pipeline.ShaderType] = new UsedPipelineData
                        {
                            CurrentlyUsing = new List<IPipeline>(),
                            Used = new Queue<IPipeline>()
                        };
                    }

                    framePipelines.CurrentlyUsing.Add(pipeline.Pipeline);
                }
            }

            mCurrentScene.Active = false;
        }

        private void BeginBatch()
        {
            using var beginBatchEvent = OptickMacros.Event();

            mCurrentScene.CurrentBatch.Shapes.Clear();
            mCurrentScene.CurrentBatch.Textures.Clear();
            mCurrentScene.CurrentBatch.Shader = typeof(BatchRender);
        }

        private static IDeviceBuffer PopOrCreateBuffer(RendererVertexData vertexData, IGraphicsContext context, DeviceBufferUsage usage, int size)
        {
            using var popOrCreateEvent = OptickMacros.Event();

            var buffer = vertexData.PopBuffer(usage, size);
            buffer ??= context.CreateDeviceBuffer(usage, size);
            return buffer;
        }

        private void SubmitScenePipeline(ulong renderTarget, Type shader, IPipeline pipeline)
        {
            using var submitPipelineEvent = OptickMacros.Event();
            if (!mCurrentScene.UsedPipelines.TryGetValue(renderTarget, out List<SceneUsedPipelineData>? renderTargetPipelineData))
            {
                renderTargetPipelineData = mCurrentScene.UsedPipelines[renderTarget] = new List<SceneUsedPipelineData>();
            }

            renderTargetPipelineData.Add(new SceneUsedPipelineData
            {
                Pipeline = pipeline,
                ShaderType = shader
            });
        }

        private void FlushBatch()
        {
            using var flushBatchEvent = OptickMacros.Event();
            if (mCommandList is null)
            {
                throw new InvalidOperationException("No frame is active!");
            }

            if (!mCurrentScene.Active)
            {
                throw new InvalidOperationException("No scene is active!");
            }

            if (mCurrentScene.CurrentBatch.Shapes.Count == 0)
            {
                return;
            }

            BeginRender();
            var renderTarget = mRenderTargets.Peek().RenderTarget;
            ulong renderTargetID = renderTarget.ID;

            int currentFrame = mSynchronizationManager.CurrentFrame;
            var frameData = mFrameData[currentFrame];
            mSynchronizationManager.ReleaseFrame(currentFrame, true);

            var vertices = new List<RendererVertex>();
            var indices = new List<uint>();

            int quadCount = 0;
            using (OptickMacros.Event("Generate batch vertices"))
            {
                foreach (var shape in mCurrentScene.CurrentBatch.Shapes)
                {
                    int previousIndexCount = indices.Count;
                    int previousVertexCount = vertices.Count;

                    shape.GetVertices(FrontFace, this, vertices, indices, out int shapeQuadCount);
                    quadCount += shapeQuadCount;

                    for (int i = previousIndexCount; i < indices.Count; i++)
                    {
                        indices[i] += (uint)previousVertexCount;
                    }
                }
            }

            int vertexBufferSize, indexBufferSize;
            IDeviceBuffer vertexStagingBuffer, vertexBuffer;
            IDeviceBuffer indexStagingBuffer, indexBuffer;

            using (OptickMacros.Event("Create batch vertex & index buffers"))
            {
                vertexBufferSize = vertices.Count * Marshal.SizeOf<RendererVertex>();
                vertexStagingBuffer = PopOrCreateBuffer(frameData.VertexData, mContext, DeviceBufferUsage.Staging, vertexBufferSize);
                vertexBuffer = PopOrCreateBuffer(frameData.VertexData, mContext, DeviceBufferUsage.Vertex, vertexBufferSize);

                indexBufferSize = indices.Count * Marshal.SizeOf<uint>();
                indexStagingBuffer = PopOrCreateBuffer(frameData.VertexData, mContext, DeviceBufferUsage.Staging, indexBufferSize);
                indexBuffer = PopOrCreateBuffer(frameData.VertexData, mContext, DeviceBufferUsage.Index, indexBufferSize);

                vertexStagingBuffer.CopyFromCPU(vertices.ToArray());
                indexStagingBuffer.CopyFromCPU(indices.ToArray());
            }

            using (OptickMacros.Event("Submit vertex & index buffer transfer list"))
            {
                var transferQueue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
                var transferList = transferQueue.Release();

                transferList.Begin();
                using (transferList.Context(GPUQueueType.Transfer))
                {
                    vertexStagingBuffer.CopyBuffers(transferList, vertexBuffer, vertexBufferSize);
                    indexStagingBuffer.CopyBuffers(transferList, indexBuffer, indexBufferSize);
                }

                SignalSemaphore(transferList);
                transferList.End();
                transferQueue.Submit(transferList);
            }

            IPipeline? pipeline = null;
            using (OptickMacros.Event("Acquire pipeline"))
            {
                if (frameData.Pipelines.TryGetValue(renderTargetID, out RenderTargetPipelineData renderTargetPipelines))
                {
                    if (renderTargetPipelines.Data.TryGetValue(mCurrentScene.CurrentBatch.Shader, out UsedPipelineData usedPipelines))
                    {
                        usedPipelines.Used.TryDequeue(out pipeline);
                    }
                }

                if (pipeline is null)
                {
                    pipeline = mLibrary.LoadPipeline(mCurrentScene.CurrentBatch.Shader, new PipelineDescription
                    {
                        RenderTarget = renderTarget,
                        Type = PipelineType.Graphics,
                        FrameCount = 1,
                        Specification = mSpecification ??= new RendererPipelineSpecification(mContext.ViewportFlipped)
                    });
                    
                    for (int i = 0; i < BatchRender.MaxTextures; i++)
                    {
                        pipeline.Bind(mWhiteTexture, nameof(BatchRender.u_Textures), i);
                    }
                }
            }

            using (OptickMacros.Event("Bind batch resources"))
            {
                if (mCurrentScene.CurrentBatch.Textures.Count > BatchRender.MaxTextures)
                {
                    throw new InvalidOperationException($"No more than {BatchRender.MaxTextures} textures may be rendered in a single batch!");
                }

                pipeline.Bind(mCameraBuffer, nameof(BatchRender.u_CameraBuffer));
                for (int i = 0; i < mCurrentScene.CurrentBatch.Textures.Count; i++)
                {
                    var texture = mCurrentScene.CurrentBatch.Textures[i];
                    pipeline.Bind(texture, nameof(BatchRender.u_Textures), i);
                }
            }

            using (OptickMacros.Event("Submit batch render commands"))
            {
                using var gpuEvent = OptickMacros.GPUEvent("Render batch");

                // the "frame" parameter only applies to pipelines used multiple frames in a row
                pipeline.Bind(mCommandList, 0);
                vertexBuffer.BindVertices(mCommandList, 0);
                indexBuffer.BindIndices(mCommandList, DeviceBufferIndexType.UInt32);
                mRenderer.RenderIndexed(mCommandList, 0, indices.Count);
            }

            using (OptickMacros.Event("Increment stats"))
            {
                mStats.DrawCalls++;
                mStats.QuadCount += quadCount;
                mStats.TexturesPushed += mCurrentScene.CurrentBatch.Textures.Count;

                mStats.VertexCount += vertices.Count;
                mStats.IndexCount += indices.Count;
            }

            using (OptickMacros.Event("Submit batch buffers & pipeline to scene"))
            {
                mCurrentScene.VertexData.PushBuffer(DeviceBufferUsage.Staging, vertexStagingBuffer);
                mCurrentScene.VertexData.PushBuffer(DeviceBufferUsage.Staging, indexStagingBuffer);

                mCurrentScene.VertexData.PushBuffer(DeviceBufferUsage.Vertex, vertexBuffer);
                mCurrentScene.VertexData.PushBuffer(DeviceBufferUsage.Index, indexBuffer);

                SubmitScenePipeline(renderTargetID, mCurrentScene.CurrentBatch.Shader, pipeline);
            }
        }

        public void NextBatch()
        {
            using var nextBatchEvent = OptickMacros.Event();

            FlushBatch();
            BeginBatch();
        }

        public void SetShader<T>() => SetShader(typeof(T));
        public void SetShader(Type type)
        {
            using var setShaderEvent = OptickMacros.Event();
            if (!mCurrentScene.Active)
            {
                throw new InvalidOperationException("No scene is active!");
            }

            if (mCurrentScene.CurrentBatch.Shader != type)
            {
                NextBatch();
                mCurrentScene.CurrentBatch.Shader = type;
            }
        }

        public void PushRenderTarget(IRenderTarget renderTarget, IFramebuffer framebuffer, Vector4 clearColor)
        {
            using var pushEvent = OptickMacros.Event();
            if (mCommandList is null)
            {
                throw new InvalidOperationException("No frame is active!");
            }

            if (mRenderTargets.TryPeek(out RenderTargetData top) && top.Active)
            {
                top.RenderTarget.EndRender(mCommandList);

                mRenderTargets.Pop();
                mRenderTargets.Push(top with
                {
                    Active = false
                });
            }

            mRenderTargets.Push(new RenderTargetData
            {
                RenderTarget = renderTarget,
                Framebuffer = framebuffer,
                ClearColor = clearColor,
                Active = false
            });
        }

        public void PopRenderTarget()
        {
            using var popEvent = OptickMacros.Event();
            if (mCommandList is null)
            {
                throw new InvalidOperationException("No frame is active!");
            }

            var top = mRenderTargets.Pop();
            if (top.Active)
            {
                top.RenderTarget.EndRender(mCommandList);
            }
        }

        public void BeginRender()
        {
            using var beginRenderEvent = OptickMacros.Event();
            if (mCommandList is null)
            {
                throw new InvalidOperationException("No frame is active!");
            }

            var top = mRenderTargets.Peek();
            if (!top.Active)
            {
                top.RenderTarget.BeginRender(mCommandList, top.Framebuffer, top.ClearColor);

                mRenderTargets.Pop();
                mRenderTargets.Push(top with
                {
                    Active = true
                });
            }
        }

        public int PushBatchTexture(ITexture texture)
        {
            using var pushTextureEvent = OptickMacros.Event();
            if (!mCurrentScene.Active)
            {
                throw new InvalidOperationException("No scene is active!");
            }

            int index = mCurrentScene.CurrentBatch.Textures.FindIndex(match => match.ID == texture.ID);
            if (index < 0)
            {
                index = mCurrentScene.CurrentBatch.Textures.Count;
                mCurrentScene.CurrentBatch.Textures.Add(texture);
            }

            return index;
        }

        public void Submit(IRenderedShape shape)
        {
            using var submitEvent = OptickMacros.Event();
            if (!mCurrentScene.Active)
            {
                throw new InvalidOperationException("No scene is active!");
            }

            mCurrentScene.CurrentBatch.Shapes.Add(shape);
        }

        private IDisposable GetSemaphore()
        {
            using var getSemaphoreEvent = OptickMacros.Event();
            if (!mUsedSemaphores.TryDequeue(out IDisposable? semaphore))
            {
                semaphore = mContext.CreateSemaphore();
            }

            return semaphore;
        }

        public void SignalSemaphore(ICommandList commandList)
        {
            using var signalEvent = OptickMacros.Event();
            var semaphore = GetSemaphore();

            commandList.AddSemaphore(semaphore, SemaphoreUsage.Signal);
            mSignaledSemaphores.Add(semaphore);
        }

        public RendererStats Stats => mStats;
        public IGraphicsContext Context => mContext;
        public ITexture WhiteTexture => mWhiteTexture;
        public ICommandList? ComamndList => mCommandList;

        private readonly List<IDisposable> mSignaledSemaphores;
        private readonly Queue<IDisposable> mUsedSemaphores;

        private readonly IGraphicsContext mContext;
        private readonly IRenderer mRenderer;
        private readonly IFrameSynchronizationManager mSynchronizationManager;

        private readonly ITexture mWhiteTexture;
        private readonly IDeviceBuffer mCameraBuffer;
        private readonly ShaderLibrary mLibrary;

        private RenderingScene mCurrentScene;
        private readonly RendererFrameData[] mFrameData;
        private readonly Stack<RenderTargetData> mRenderTargets;
        private ICommandList? mCommandList;

        private IPipelineSpecification? mSpecification;
        private RendererStats mStats;
        private bool mDisposed;
    }

    public sealed class RenderedQuad : IRenderedShape
    {
        private static readonly Vector2[] sFactors;
        private static readonly uint[] mClockwiseIndices, mCounterClockwiseIndices;
        static RenderedQuad()
        {
            sFactors = new Vector2[]
            {
                new Vector2(1f, 1f),
                new Vector2(1f, -1f),
                new Vector2(-1f, -1f),
                new Vector2(-1f, 1f)
            };

            mClockwiseIndices = new uint[]
            {
                0, 1, 3,
                1, 2, 3
            };

            mCounterClockwiseIndices = new uint[]
            {
                3, 1, 0,
                3, 2, 1
            };
        }

        public RenderedQuad()
        {
            using var constructorEvent = OptickMacros.Event();

            mCenter = Vector2.Zero;
            mHalfSize = Vector2.One * 0.5f;
            mRotation = 0f;

            mColor = Vector4.One;
            mTexture = null;
        }

        public Vector2 Position
        {
            get => mCenter;
            set => mCenter = value;
        }

        public Vector2 Size
        {
            get => mHalfSize * 2f;
            set => mHalfSize = value * 0.5f;
        }

        public float RotationDegrees
        {
            get => mRotation * 180f / MathF.PI;
            set => mRotation = value * MathF.PI / 180f;
        }

        public float RotationRadians
        {
            get => mRotation;
            set => mRotation = value;
        }

        public Vector4 Color
        {
            get => mColor;
            set => mColor = value;
        }

        public ITexture? Texture
        {
            get => mTexture;
            set => mTexture = value;
        }

        public void GetVertices(PipelineFrontFace frontFace, BatchRenderer renderer, IList<RendererVertex> vertices, IList<uint> indices, out int quadCount)
        {
            using var getVerticesEvent = OptickMacros.Event();
            quadCount = 1;

            int textureIndex = renderer.PushBatchTexture(mTexture ?? renderer.WhiteTexture);
            float cos = MathF.Cos(mRotation);
            float sin = MathF.Sin(mRotation);

            using (OptickMacros.Event("Compute vertices"))
            {
                for (int i = 0; i < sFactors.Length; i++)
                {
                    var factor = sFactors[i];
                    var scaledSize = factor * mHalfSize;

                    var uv = factor / 2f + Vector2.One * 0.5f;
                    vertices.Add(new RendererVertex
                    {
                        Position = mCenter + new Vector2
                        {
                            X = scaledSize.X * cos - scaledSize.Y * sin,
                            Y = scaledSize.X * sin + scaledSize.Y * cos
                        },
                        Color = mColor,
                        UV = renderer.Context.FlipUVs ? new Vector2(uv.X, 1f - uv.Y) : uv,
                        TextureIndex = textureIndex
                    });
                }
            }

            using (OptickMacros.Event("Determine indices"))
            {
                var quadIndices = frontFace switch
                {
                    PipelineFrontFace.Clockwise => mClockwiseIndices,
                    PipelineFrontFace.CounterClockwise => mCounterClockwiseIndices,
                    _ => throw new ArgumentException("Invalid front face!")
                };

                foreach (uint index in quadIndices)
                {
                    indices.Add(index);
                }
            }
        }

        private Vector2 mCenter, mHalfSize;
        private float mRotation;

        private Vector4 mColor;
        private ITexture? mTexture;
    }
}