using CodePlayground.Graphics;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ChessAI.GUI
{
    public struct RendererStats
    {
        public int DrawCalls;
        public int ShapeCount;

        public int VertexCount;
        public int IndexCount;
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
        public void GetVertices(PipelineFrontFace frontFace, BatchRenderer renderer, out RendererVertex[] vertices, out int[] indices);
    }

    public sealed class RenderedQuad : IRenderedShape
    {
        public RenderedQuad()
        {
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

        public void GetVertices(PipelineFrontFace frontFace, BatchRenderer renderer, out RendererVertex[] vertices, out int[] indices)
        {
            var factors = new Vector2[]
            {
                new Vector2(1f, 1f),
                new Vector2(1f, -1f),
                new Vector2(-1f, -1f),
                new Vector2(-1f, 1f)
            };

            int textureIndex = -1;
            if (mTexture is not null)
            {
                textureIndex = renderer.GetBatchTextureID(mTexture);
            }

            float cos = MathF.Cos(mRotation);
            float sin = MathF.Sin(mRotation);

            vertices = new RendererVertex[factors.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var factor = factors[i];
                var scaledSize = factor * mHalfSize;

                var uv = factor / 2f + Vector2.One * 0.5f;
                vertices[i] = new RendererVertex
                {
                    Position = mCenter + new Vector2
                    {
                        X = scaledSize.X * cos - scaledSize.Y * sin,
                        Y = scaledSize.X * sin + scaledSize.Y * cos
                    },
                    Color = mColor,
                    UV = renderer.Context.FlipUVs ? new Vector2(uv.X, 1f - uv.Y) : uv,
                    TextureIndex = textureIndex
                };
            }

            indices = frontFace switch
            {
                PipelineFrontFace.Clockwise => new int[]
                {

                },
                PipelineFrontFace.CounterClockwise => new int[]
                {

                },
                _ => throw new ArgumentException("Invalid front face!")
            };
        }

        private Vector2 mCenter, mHalfSize;
        private float mRotation;

        private Vector4 mColor;
        private ITexture? mTexture;
    }

    // based off of
    // https://github.com/yodasoda1219/sge/blob/main/sge/src/sge/renderer/renderer.h
    public sealed class BatchRenderer : IDisposable
    {
        public BatchRenderer(IGraphicsContext context, IRenderer renderer)
        {
            mDisposed = false;

            mContext = context;
            mRenderer = renderer;

            throw new NotImplementedException();
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
            throw new NotImplementedException();
        }

        public void NewFrame()
        {
            throw new NotImplementedException();
        }

        public void Wait() => mContext.Device.Wait();

        public void ClearRenderData()
        {
            throw new NotImplementedException();
        }

        public void BeginScene(Matrix4x4 viewProjection)
        {
            throw new NotImplementedException();
        }

        public void EndScene()
        {
            throw new NotImplementedException();
        }

        public void SetCommandList(ICommandList commandList)
        {
            throw new NotImplementedException();
        }

        public void SetShader<T>() => SetShader(typeof(T));
        public void SetShader(Type type)
        {
            throw new NotImplementedException();
        }

        public void PushRenderTarget(IRenderTarget renderTarget, IFramebuffer framebuffer, Vector4 clearColor)
        {
            throw new NotImplementedException();
        }

        public void PopRenderTarget()
        {
            throw new NotImplementedException();
        }

        public void BeginRenderPass()
        {
            throw new NotImplementedException();
        }

        public int GetBatchTextureID(ITexture texture)
        {
            throw new NotImplementedException();
        }

        // position is centered
        public void DrawQuad(Vector2 position, Vector2 size, Vector4 color, ITexture? texture = null)
        {
            throw new NotImplementedException();
        }

        public RendererStats Stats => throw new NotImplementedException();
        public IGraphicsContext Context => mContext;

        private readonly IGraphicsContext mContext;
        private readonly IRenderer mRenderer;
        private bool mDisposed;
    }
}