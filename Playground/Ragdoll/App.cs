using CodePlayground;
using CodePlayground.Graphics;
using Optick.NET;
using Ragdoll.Layers;
using System.Numerics;

namespace Ragdoll
{
    [ApplicationTitle("Ragdoll")]
    internal sealed class App : GraphicsApplication
    {
        public static new App Instance
        {
            get
            {
                var application = Application.Instance;
                return (App)application;
            }
        }

        public App()
        {
            mLayerStack = new LayerStack();
            mRenderer = null;
            mModelRegistry = null;

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

            // enable profiling
            InitializeOptick();
            var loadEvent = OptickMacros.Event();

            mRenderer = new Renderer(context);
            mModelRegistry = new ModelRegistry(context);

            mLayerStack.PushLayer<SceneLayer>(LayerType.Layer);
            InitializeImGui();
        }

        private void OnInputReady() => InitializeImGui();
        private void InitializeImGui()
        {
            var initializeEvent = OptickMacros.Event();

            var graphicsContext = GraphicsContext;
            var inputContext = InputContext;
            var window = RootWindow;

            if (graphicsContext is null ||
                inputContext is null ||
                window is null ||
                mLayerStack.HasLayer<ImGuiLayer>())
            {
                return;
            }

            var renderTarget = graphicsContext.Swapchain.RenderTarget;
            mLayerStack.PushLayer<ImGuiLayer>(LayerType.Overlay, graphicsContext, inputContext, window, renderTarget);
        }

        private void OnClose()
        {
            var closeEvent = OptickMacros.Event();

            var device = GraphicsContext?.Device;
            device?.ClearQueues();

            mLayerStack.Clear();
            mModelRegistry?.Dispose();
            mRenderer?.Dispose();
        }

        private void OnUpdate(double delta)
        {
            mLayerStack.EnumerateLayers(layer => layer.OnUpdate(delta));
            if (mLayerStack.HasLayer<ImGuiLayer>())
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

        public ILayerView LayerView => mLayerStack;
        public Renderer? Renderer => mRenderer;
        public ModelRegistry? ModelRegistry => mModelRegistry;

        private Renderer? mRenderer;
        private ModelRegistry? mModelRegistry;
        private readonly LayerStack mLayerStack;
    }
}