using CodePlayground;
using CodePlayground.Graphics;
using Optick.NET;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

namespace MachineLearning
{
    [ApplicationTitle("Machine learning test")]
    internal sealed class App : GraphicsApplication
    {
        public const int FrameCount = 3;

        public static new App Instance => (App)Application.Instance;
        public static Random RNG => sRandom;
        
        private static readonly Random sRandom;
        static App()
        {
            sRandom = new Random();
        }

        public App()
        {
            mExistingSemaphores = new Queue<IDisposable>();
            mSignaledSemaphores = new List<IDisposable>();

            Load += OnLoad;
            InputReady += OnInputReady;
            Closing += OnClose;

            Update += OnUpdate;
            Render += OnRender;
        }

        private void OnLoad()
        {
            var context = CreateGraphicsContext();
            context.Swapchain.VSync = true;

            mLibrary = new ShaderLibrary(context, Assembly.GetExecutingAssembly());
            mRenderer = context.CreateRenderer();

            InitializeOptick();
            InitializeImGui();
        }

        private void OnInputReady() => InitializeImGui();
        private void InitializeImGui()
        {
            var graphicsContext = GraphicsContext;
            var inputContext = InputContext;
            var window = RootWindow;

            if (mImGui is not null || window is null || graphicsContext is null || inputContext is null)
            {
                return;
            }

            var queue = graphicsContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            using (commandList.Context(GPUQueueType.Transfer))
            {
                mImGui = new ImGuiController(graphicsContext, inputContext, window, graphicsContext.Swapchain.RenderTarget, FrameCount);
                mImGui.LoadFontAtlas(commandList);
            }

            SignalSemaphore(commandList);

            commandList.End();
            queue.Submit(commandList);
        }

        private IDisposable GetSemaphore()
        {
            if (!mExistingSemaphores.TryDequeue(out IDisposable? semaphore))
            {
                semaphore = GraphicsContext!.CreateSemaphore();
            }

            return semaphore;
        }

        public void SignalSemaphore(ICommandList commandList)
        {
            var semaphore = GetSemaphore();
            mSignaledSemaphores.Add(semaphore);

            commandList.AddSemaphore(semaphore, SemaphoreUsage.Signal);
        }

        private void OnClose()
        {
            var context = GraphicsContext;
            context?.Device?.ClearQueues();

            while (mExistingSemaphores.Count > 0)
            {
                var semaphore = mExistingSemaphores.Dequeue();
                semaphore.Dispose();
            }

            foreach (var semaphore in mSignaledSemaphores)
            {
                semaphore.Dispose();
            }

            mLibrary?.Dispose();
            mImGui?.Dispose();
            GraphicsContext?.Dispose();
        }

        private void OnUpdate(double delta)
        {
            mImGui?.NewFrame(delta);

            // todo: update menus
        }

        private void OnRender(FrameRenderInfo renderInfo)
        {
            var commandList = renderInfo.CommandList!;
            var renderTarget = renderInfo.RenderTarget!;

            foreach (var semaphore in mSignaledSemaphores)
            {
                commandList.AddSemaphore(semaphore, SemaphoreUsage.Wait);
            }

            renderTarget.BeginRender(commandList, renderInfo.Framebuffer!, Vector4.Zero);
            mImGui?.Render(commandList, mRenderer!, mCurrentFrame);
            renderTarget.EndRender(commandList);

            mCurrentFrame = (mCurrentFrame + 1) % FrameCount;
            mSignaledSemaphores.Clear();
        }

        private ImGuiController? mImGui;
        private IRenderer? mRenderer;

        private ShaderLibrary? mLibrary;
        private int mCurrentFrame;

        private readonly Queue<IDisposable> mExistingSemaphores;
        private readonly List<IDisposable> mSignaledSemaphores;
    }
}