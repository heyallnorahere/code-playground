using CodePlayground.Graphics;
using Optick.NET;
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
            using var pushedEvent = OptickMacros.Event();
            mController = new ImGuiController(mGraphicsContext, mInputContext, mWindow, mRenderTarget, Renderer.FrameCount);

            mUseSemaphore = true;
            mSemaphore = mGraphicsContext.CreateSemaphore();

            var device = mGraphicsContext.Device;
            var queue = device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            commandList.AddSemaphore(mSemaphore, SemaphoreUsage.Signal);

            using (commandList.Context(GPUQueueType.Transfer))
            {
                mController.LoadFontAtlas(commandList);
            }

            commandList.End();
            queue.Submit(commandList);
        }

        public override void OnPopped()
        {
            using var poppedEvent = OptickMacros.Event();

            mController?.Dispose();
            mSemaphore?.Dispose();

            mUseSemaphore = false;
        }

        public override void OnUpdate(double delta)
        {
            using var updateEvent = OptickMacros.Event();
            mController?.NewFrame(delta);
        }

        public override void OnRender(Renderer renderer)
        {
            using var renderEvent = OptickMacros.Event();

            var frameInfo = renderer.FrameInfo;
            var commandList = frameInfo.CommandList;

            mController?.Render(commandList, renderer.API, frameInfo.CurrentFrame);
            if (mUseSemaphore && mSemaphore is not null)
            {
                commandList.AddSemaphore(mSemaphore, SemaphoreUsage.Wait);
                mUseSemaphore = false;
            }
        }

        public ImGuiController Controller => mController!;

        private readonly IGraphicsContext mGraphicsContext;
        private readonly IInputContext mInputContext;
        private readonly IWindow mWindow;
        private readonly IRenderTarget mRenderTarget;

        private bool mUseSemaphore;
        private IDisposable? mSemaphore;

        private ImGuiController? mController;
    }
}