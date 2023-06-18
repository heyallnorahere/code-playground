using CodePlayground;
using CodePlayground.Graphics;
using Ragdoll.Layers;
using System.Numerics;

namespace Ragdoll
{
    [ApplicationTitle("Ragdoll")]
    internal sealed class App : GraphicsApplication
    {
        public App()
        {
            mLayerStack = new LayerStack();
            mRenderer = null;

            Load += OnLoad;
            InputReady += OnInputReady;
            Closing += OnClose;
            Update += OnUpdate;
            Render += OnRender;
        }

        private void OnLoad()
        {
            var context = CreateGraphicsContext();
            mRenderer = new Renderer(context);

            InitializeImGui();
        }

        private void OnInputReady() => InitializeImGui();
        private void InitializeImGui()
        {
            var graphicsContext = GraphicsContext;
            var inputContext = InputContext;
            var window = RootWindow;

            if (graphicsContext is null ||
                inputContext is null ||
                window is null ||
                mLayerStack.FindLayer<ImGuiLayer>() is not null)
            {
                return;
            }

            var renderTarget = graphicsContext.Swapchain.RenderTarget;
            mLayerStack.PushLayer<ImGuiLayer>(LayerType.Overlay, graphicsContext, inputContext, window, renderTarget);
        }

        private void OnClose()
        {
            mLayerStack.Clear();
            if (mRenderer is not null)
            {
                var context = mRenderer.Context;
                mRenderer.Dispose();
                context.Dispose(); // renderer doesn't own context
            }
        }

        private void OnUpdate(double delta)
        {
            mLayerStack.EnumerateLayers(layer => layer.OnUpdate(delta));
            if (mLayerStack.FindLayer<ImGuiLayer>() is not null)
            {
                mLayerStack.EnumerateLayers(layer => layer.OnImGuiRender());
            }
        }

        private void OnRender(FrameRenderInfo renderInfo)
        {
            if (mRenderer is null ||
                renderInfo.CommandList is null ||
                renderInfo.RenderTarget is null ||
                renderInfo.Framebuffer is null)
            {
                return;
            }

            mRenderer.BeginFrame(renderInfo.CommandList);
            mLayerStack.EnumerateLayers(layer => layer.PreRender(mRenderer));

            mRenderer.BeginRender(renderInfo.RenderTarget, renderInfo.Framebuffer, new Vector4(0.2f, 0.2f, 0.2f, 1f));
            mLayerStack.EnumerateLayers(layer => layer.OnRender(mRenderer));
            mRenderer.EndRender();
        }

        private Renderer? mRenderer;
        private readonly LayerStack mLayerStack;
    }
}