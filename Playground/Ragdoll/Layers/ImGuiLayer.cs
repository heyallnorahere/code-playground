using CodePlayground.Graphics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Windowing;
using System;

namespace Ragdoll.Layers
{
    internal sealed class ImGuiLayer : Layer
    {
        public ImGuiLayer(IGraphicsContext graphicsContext, IInputContext inputContext, IWindow window, IRenderTarget renderTarget)
        {
            mGraphicsContext = graphicsContext;
            mInputContext = inputContext;
            mWindow = window;
            mRenderTarget = renderTarget;

            mUseSemaphore = false;
            mSemaphore = null;

            mController = null;
        }

        public override void OnPushed()
        {
            mController = new ImGuiController(mGraphicsContext, mInputContext, mWindow, mRenderTarget, Renderer.FrameCount);

            mUseSemaphore = true;
            mSemaphore = mGraphicsContext.CreateSemaphore();

            var device = mGraphicsContext.Device;
            var queue = device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();
            commandList.Begin();

            mController.LoadFontAtlas(commandList);
            commandList.AddSemaphore(mSemaphore, SemaphoreUsage.Signal);

            commandList.End();
            queue.Submit(commandList);
        }

        public override void OnPopped()
        {
            mController?.Dispose();
            mSemaphore?.Dispose();

            mUseSemaphore = false;
        }

        public override void OnUpdate(double delta)
        {
            mController?.NewFrame(delta);
        }

        public override void OnRender(Renderer renderer)
        {
            var frameInfo = renderer.FrameInfo;
            var commandList = frameInfo.CommandList;

            mController?.Render(commandList, renderer.API, frameInfo.CurrentFrame);
            if (mUseSemaphore && mSemaphore is not null)
            {
                commandList.AddSemaphore(mSemaphore, SemaphoreUsage.Wait);
                mUseSemaphore = false;
            }
        }

        private readonly IGraphicsContext mGraphicsContext;
        private readonly IInputContext mInputContext;
        private readonly IWindow mWindow;
        private readonly IRenderTarget mRenderTarget;

        private bool mUseSemaphore;
        private IDisposable? mSemaphore;

        private ImGuiController? mController;
    }
}