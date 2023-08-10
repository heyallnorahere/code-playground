using CodePlayground.Graphics.Vulkan;
using Optick.NET;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using System;
using System.Diagnostics.CodeAnalysis;

namespace CodePlayground.Graphics
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ApplicationWindowSizeAttribute : ApplicationDescriptionAttribute
    {
        public ApplicationWindowSizeAttribute(int width, int height)
        {
            mInitialSize = new Vector2D<int>(width, height);
        }

        public override void Apply(Application application)
        {
            if (application is GraphicsApplication graphicsApp)
            {
                graphicsApp.mInitialSize = mInitialSize;
            }
        }

        private readonly Vector2D<int> mInitialSize;
    }

    public enum AppGraphicsAPI
    {
        OpenGL,
        Vulkan,
        Other
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ApplicationGraphicsAPIAttribute : ApplicationDescriptionAttribute
    {
        public ApplicationGraphicsAPIAttribute(AppGraphicsAPI api)
        {
            mAPI = api;
        }

        public override void Apply(Application application)
        {
            if (application is GraphicsApplication graphicsApp)
            {
                graphicsApp.mAPI = mAPI;
            }
        }

        private readonly AppGraphicsAPI mAPI;
    }

    public struct FrameRenderInfo
    {
        public double Delta { get; set; }
        public ICommandList? CommandList { get; set; }
        public IRenderTarget? RenderTarget { get; set; }
        public IFramebuffer? Framebuffer { get; set; }
        public int CurrentImage { get; set; }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    [RequestedVulkanExtension(ExtDebugUtils.ExtensionName, VulkanExtensionLevel.Instance, VulkanExtensionType.Extension, Required = false)]
    [RequestedVulkanExtension(KhrGetPhysicalDeviceProperties2.ExtensionName, VulkanExtensionLevel.Instance, VulkanExtensionType.Extension, Required = false)]
    [RequestedVulkanExtension(KhrSwapchain.ExtensionName, VulkanExtensionLevel.Device, VulkanExtensionType.Extension, Required = true)]
    [RequestedVulkanExtension("VK_KHR_portability_subset", VulkanExtensionLevel.Device, VulkanExtensionType.Extension, Required = false)]
    [RequestedVulkanExtension("VK_LAYER_KHRONOS_validation", VulkanExtensionLevel.Instance, VulkanExtensionType.Layer, Required = false)]
    public abstract class GraphicsApplication : Application
    {
        public const int SynchronizationFrames = 2;
        public const string MainThreadName = "Main loop";

        public GraphicsApplication()
        {
            Utilities.BindHandlers(this, this);

            mExitCode = 0;
            mIsRunning = false;
            mAPI = AppGraphicsAPI.Vulkan;
            mInitialSize = new Vector2D<int>(800, 600);

            mWindow = null;
            mInputContext = null;
            mArgs = Array.Empty<string>();
        }

        public override bool IsRunning => mIsRunning;
        protected override string[] CopiedBinaries => new string[]
        {
            "glfw3.dll",
            "libglfw.so.3",
            "libglfw.3.dylib"
        };

        public override bool Quit(int exitCode)
        {
            if (mWindow?.IsClosing ?? true)
            {
                return false;
            }

            mWindow?.Close();
            mExitCode = exitCode;

            return true;
        }

        protected override int Run(string[] args)
        {
            mArgs = args;
            mOptions = WindowOptions.Default with
            {
                Size = mInitialSize,
                Title = Title,
                API = mAPI switch
                {
                    AppGraphicsAPI.OpenGL => GraphicsAPI.Default,
                    AppGraphicsAPI.Vulkan => GraphicsAPI.DefaultVulkan,
                    _ => GraphicsAPI.None
                }
            };

            mWindow = Window.Create(mOptions.Value);
            RegisterWindowEvents();

            mIsRunning = true;
            mWindow.Run();
            mIsRunning = false;

            mWindow.Dispose();
            mWindow = null;

            return mExitCode;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                mGraphicsContext?.Dispose();
                mWindow?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void RegisterWindowEvents()
        {
            if (mWindow is null)
            {
                return;
            }

            mWindow.Resize += size => WindowResize?.Invoke(size);
            mWindow.FramebufferResize += size => FramebufferResize?.Invoke(size);
            mWindow.Closing += () => Closing?.Invoke();
            mWindow.FocusChanged += focused => FocusChanged?.Invoke(focused);
            mWindow.Load += () => Load?.Invoke();
            mWindow.Update += OnUpdate;
            mWindow.Render += OnRender;
        }

        public event Action<Vector2D<int>>? WindowResize;
        public event Action<Vector2D<int>>? FramebufferResize;
        public event Action? Closing;
        public event Action<bool>? FocusChanged;
        public event Action? Load;
        public event Action<double>? Update;
        public event Action<FrameRenderInfo>? Render;

        public event Action? InputReady;

        [EventHandler(nameof(Load))]
        private void OnLoad()
        {
            try
            {
                mInputContext = mWindow!.CreateInput();
                InputReady?.Invoke();
            }
            catch (Exception)
            {
                mInputContext = null;
            }
        }

        [EventHandler(nameof(Closing))]
        private void OnClose()
        {
            mFrameEvent?.Dispose();
            mFrameEvent = null;

            mInputContext?.Dispose();
        }

        // not an event handler - we need to be able to call render under certain circumstances
        private void OnUpdate(double delta)
        {
            mFrameEvent?.Dispose();
            mFrameEvent = OptickMacros.Frame(MainThreadName);

            using var updateEvent = OptickMacros.Category("Update", Category.GameLogic);
            Update?.Invoke(delta);
        }

        private void OnRender(double delta)
        {
            using (var renderEvent = OptickMacros.Category("Render", Category.Rendering))
            {
                if (mGraphicsContext is null)
                {
                    Render?.Invoke(new FrameRenderInfo
                    {
                        Delta = delta,
                        CommandList = null,
                        RenderTarget = null,
                        Framebuffer = null,
                        CurrentImage = -1
                    });
                }
                else
                {
                    var device = mGraphicsContext.Device;
                    var swapchain = mGraphicsContext.Swapchain;
                    swapchain.AcquireImage();

                    var queue = device.GetQueue(CommandQueueFlags.Graphics);
                    var commandList = queue.Release();
                    commandList.Begin();

                    using (var context = new GPUContextScope(commandList.Address))
                    {
                        Render?.Invoke(new FrameRenderInfo
                        {
                            Delta = delta,
                            CommandList = commandList,
                            RenderTarget = swapchain.RenderTarget,
                            Framebuffer = swapchain.CurrentFramebuffer,
                            CurrentImage = swapchain.CurrentFrame
                        });
                    }

                    commandList.End();
                    swapchain.Present(queue, commandList);
                }
            }

            mFrameEvent?.Dispose();
            mFrameEvent = null;
        }

        public IGraphicsContext CreateGraphicsContext()
        {
            return mAPI switch
            {
                AppGraphicsAPI.Vulkan => CreateGraphicsContext<VulkanContext>(),
                AppGraphicsAPI.Other => throw new InvalidOperationException(),
                _ => throw new NotImplementedException()
            };
        }

        internal void OnContextDestroyed()
        {
            ShutdownOptick();
            // what else? idk
        }

        protected virtual void OnContextCreation(IGraphicsContext context) { }
        public T CreateGraphicsContext<T>(params object[] args) where T : IGraphicsContext
        {
            if (mWindow is null || mOptions is null)
            {
                throw new InvalidOperationException("The window has not been created!");
            }

            if (mGraphicsContext is not null)
            {
                throw new InvalidOperationException("A graphics context has already been created!");
            }

            var context = Utilities.CreateDynamicInstance<T>(args);
            if (!context.IsApplicable(mOptions.Value))
            {
                throw new InvalidOperationException("Context type is not applicable to this window!");
            }

            OnContextCreation(context);
            context.Initialize(mWindow, this);

            mGraphicsContext = context;
            return context;
        }

        public IWindow? RootWindow => mWindow;
        public IInputContext? InputContext => mInputContext;
        public IGraphicsContext? GraphicsContext => mGraphicsContext;
        public string[] CommandLineArguments => mArgs;
        internal IVkSurface? VulkanSurfaceFactory => mWindow?.VkSurface;

        private int mExitCode;
        private bool mIsRunning;
        internal AppGraphicsAPI mAPI;
        internal Vector2D<int> mInitialSize;

        private IWindow? mWindow;
        private WindowOptions? mOptions;
        private IInputContext? mInputContext;
        private IGraphicsContext? mGraphicsContext;
        private string[] mArgs;
        private Event? mFrameEvent;
    }
}