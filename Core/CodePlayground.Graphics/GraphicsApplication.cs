﻿using CodePlayground.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Maths;
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
                graphicsApp.mAPI = mAPI switch
                {
                    AppGraphicsAPI.OpenGL => GraphicsAPI.Default,
                    AppGraphicsAPI.Vulkan => GraphicsAPI.DefaultVulkan,
                    AppGraphicsAPI.Other => GraphicsAPI.None,
                    _ => throw new ArgumentException("Invalid graphics API!")
                };
            }
        }

        private readonly AppGraphicsAPI mAPI;
    }

    public interface IGraphicsContext : IDisposable
    {
        public bool IsApplicable(WindowOptions options);
        public void Initialize(IWindow window, GraphicsApplication application);
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    [RequestedVulkanExtension("VK_EXT_debug_utils", VulkanExtensionLevel.Instance, VulkanExtensionType.Extension, Required = false)]
    [RequestedVulkanExtension("VK_LAYER_KHRONOS_validation", VulkanExtensionLevel.Instance, VulkanExtensionType.Layer, Required = false)]
    [RequestedVulkanExtension("VK_KHR_swapchain", VulkanExtensionLevel.Device, VulkanExtensionType.Extension, Required = true)]
    public abstract class GraphicsApplication : Application
    {
        public GraphicsApplication()
        {
            Utilities.BindHandlers(this, this);

            mExitCode = 0;
            mIsRunning = false;
            mAPI = GraphicsAPI.Default;
            mInitialSize = new Vector2D<int>(800, 600);

            mWindow = null;
            mInputContext = null;
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
            mOptions = WindowOptions.Default with
            {
                Size = mInitialSize,
                Title = Title,
                API = mAPI
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
            mWindow.Update += delta => Update?.Invoke(delta);
            mWindow.Render += delta => Render?.Invoke(delta);
        }

        public event Action<Vector2D<int>>? WindowResize;
        public event Action<Vector2D<int>>? FramebufferResize;
        public event Action? Closing;
        public event Action<bool>? FocusChanged;
        public event Action? Load;
        public event Action<double>? Update;
        public event Action<double>? Render;

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
            mInputContext?.Dispose();
        }

        public T CreateGraphicsContext<T>() where T : IGraphicsContext, new()
        {
            if (mWindow is null || mOptions is null)
            {
                throw new InvalidOperationException("The window has not been created!");
            }

            if (mGraphicsContext is not null)
            {
                throw new InvalidOperationException("A graphics context has already been created!");
            }

            var context = new T();
            if (!context.IsApplicable(mOptions.Value))
            {
                throw new InvalidOperationException("Context type is not applicable to this window!");
            }

            context.Initialize(mWindow, this);
            mGraphicsContext = context;
            return context;
        }

        public IInputContext? InputContext => mInputContext;
        public IGraphicsContext? GraphicsContext => mGraphicsContext;
        internal IVkSurface? VulkanSurfaceFactory => mWindow?.VkSurface;

        private int mExitCode;
        private bool mIsRunning;
        internal GraphicsAPI mAPI;
        internal Vector2D<int> mInitialSize;

        private IWindow? mWindow;
        private WindowOptions? mOptions;
        private IInputContext? mInputContext;
        private IGraphicsContext? mGraphicsContext;
    }
}