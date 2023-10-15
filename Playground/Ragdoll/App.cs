using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Vulkan;
using Optick.NET;
using Ragdoll.Layers;
using Silk.NET.Vulkan.Extensions.NV;
using System.Numerics;

namespace Ragdoll
{
    [ApplicationTitle("Ragdoll")]
    [RequestedVulkanExtension(NVDeviceDiagnosticCheckpoints.ExtensionName, VulkanExtensionLevel.Device, VulkanExtensionType.Extension, Required = false)]
    internal sealed class App : GraphicsApplication
    {
        public static int Main(string[] args) => RunApplication<App>(args);

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
            var swapchain = context.Swapchain;

            if (swapchain is not null)
            {
                swapchain.VSync = true;
            }

            // enable profiling
            InitializeOptick();
            using var loadEvent = OptickMacros.Event();

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
            var renderTarget = graphicsContext?.Swapchain?.RenderTarget;

            if (graphicsContext is null ||
                inputContext is null ||
                window is null ||
                renderTarget is null ||
                mLayerStack.HasLayer<ImGuiLayer>())
            {
                return;
            }

            mLayerStack.PushLayer<ImGuiLayer>(LayerType.Overlay, graphicsContext, inputContext, window, renderTarget);
        }

        private void OnClose()
        {
            using var closeEvent = OptickMacros.Event();
            mLayerStack.Clear();

            var device = GraphicsContext?.Device;
            device?.ClearQueues();

            mModelRegistry?.Dispose();
            mRenderer?.Dispose();

            GraphicsContext?.Dispose();
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