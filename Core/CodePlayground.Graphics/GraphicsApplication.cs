using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
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

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ApplicationUsesOpenGLAttribute : ApplicationDescriptionAttribute
    {
        public override void Apply(Application application)
        {
            if (application is GraphicsApplication graphicsApp)
            {
                graphicsApp.mUseOpenGL = true;
            }
        }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    public abstract class GraphicsApplication : Application
    {
        public GraphicsApplication()
        {
            Utilities.BindHandlers(this, this);

            mExitCode = 0;
            mIsRunning = false;
            mUseOpenGL = false;
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
            var options = WindowOptions.Default with
            {
                Size = mInitialSize,
                Title = Title,
                API = mUseOpenGL ? GraphicsAPI.Default : GraphicsAPI.None
            };

            mWindow = Window.Create(options);
            RegisterWindowEvents();

            mIsRunning = true;
            mWindow.Run();
            mIsRunning = false;

            return mExitCode;
        }

        protected override void Dispose(bool disposing)
        {
            mWindow?.Dispose();
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

        public IInputContext? InputContext => mInputContext;

        private int mExitCode;
        private bool mIsRunning;
        internal bool mUseOpenGL;
        internal Vector2D<int> mInitialSize;

        private IWindow? mWindow;
        private IInputContext? mInputContext;
    }
}