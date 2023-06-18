using CodePlayground.Graphics;
using System;
using System.Numerics;

namespace Ragdoll
{
    internal struct RendererRenderInfo
    {
        public IRenderTarget RenderTarget { get; set; }
        public IFramebuffer Framebuffer { get; set; }
    }

    internal struct RendererFrameInfo
    {
        public ICommandList CommandList { get; set; }
        public int CurrentFrame { get; set; }
        public RendererRenderInfo? RenderInfo { get; set; }
    }

    internal sealed class Renderer : IDisposable
    {
        public const int FrameCount = 2;

        public Renderer(IGraphicsContext context)
        {
            //! does NOT own context
            mContext = context;
            mRenderer = mContext.CreateRenderer();
            mLibrary = new ShaderLibrary(mContext, GetType().Assembly);

            mDisposed = false;
            mFrameInfo.CurrentFrame = -1;
        }

        ~Renderer()
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
                mLibrary.Dispose();
            }
        }

        public void BeginFrame(ICommandList commandList)
        {
            if (mFrameInfo.RenderInfo is not null)
            {
                throw new InvalidOperationException("Cannot start a new frame while rendering!");
            }

            mFrameInfo.CommandList = commandList;
            mFrameInfo.CurrentFrame = (mFrameInfo.CurrentFrame + 1) % FrameCount;
        }

        private void VerifyFrame()
        {
            if (mFrameInfo.CurrentFrame >= 0)
            {
                return;
            }

            throw new InvalidOperationException("A frame has not been started!");
        }

        public void BeginRender(IRenderTarget renderTarget, IFramebuffer framebuffer, Vector4 clearColor)
        {
            VerifyFrame();
            if (mFrameInfo.RenderInfo is not null)
            {
                throw new InvalidOperationException("Rendering has already begun!");
            }

            renderTarget.BeginRender(mFrameInfo.CommandList, framebuffer, clearColor);
            mFrameInfo.RenderInfo = new RendererRenderInfo
            {
                RenderTarget = renderTarget,
                Framebuffer = framebuffer
            };
        }

        public void EndRender()
        {
            VerifyFrame();
            if (mFrameInfo.RenderInfo is null)
            {
                return;
            }

            var renderInfo = mFrameInfo.RenderInfo.Value;
            renderInfo.RenderTarget.EndRender(mFrameInfo.CommandList);

            mFrameInfo.RenderInfo = null;
        }

        public IGraphicsContext Context => mContext;
        public IRenderer API => mRenderer;
        public ShaderLibrary Library => mLibrary;
        public RendererFrameInfo FrameInfo => mFrameInfo;

        private readonly ShaderLibrary mLibrary;
        private readonly IRenderer mRenderer;
        private readonly IGraphicsContext mContext;

        private RendererFrameInfo mFrameInfo;
        private bool mDisposed;
    }
}