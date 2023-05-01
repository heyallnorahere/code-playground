using Silk.NET.Maths;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;

namespace CodePlayground.Graphics
{
    public enum DeviceType
    {
        Discrete,
        Integrated,
        Virtual,
        CPU,
        Other
    }

    [Flags]
    public enum CommandQueueFlags
    {
        Graphics = 0x1,
        Compute = 0x2,
        Transfer = 0x4
    }

    public enum AttachmentType
    {
        Color,
        DepthStencil
    }

    public interface IGraphicsContext : IDisposable
    {
        public IGraphicsDeviceScorer DeviceScorer { set; }
        public IGraphicsDevice Device { get; }
        public ISwapchain Swapchain { get; }

        public bool IsApplicable(WindowOptions options);
        public void Initialize(IWindow window, GraphicsApplication application);
    }

    public interface IGraphicsDeviceInfo
    {
        public string Name { get; }
        public DeviceType Type { get; }
    }

    public interface IGraphicsDeviceScorer
    {
        public int ScoreDevice(IGraphicsDeviceInfo device, IGraphicsContext context);
    }

    public interface IGraphicsDevice
    {
        public IGraphicsDeviceInfo DeviceInfo { get; }
        public ICommandQueue GetQueue(CommandQueueFlags usage);
    }

    public interface ICommandList
    {
        public bool IsRecording { get; }
        public void Begin();
        public void End();
    }

    public interface ICommandQueue
    {
        public CommandQueueFlags Usage { get; }
        public int CommandListCap { get; set; }

        public ICommandList Release();
        public void Submit(ICommandList commandList);
        public void Wait();
    }

    public interface IRenderTarget : IDisposable
    {
        public IReadOnlyList<AttachmentType> AttachmentTypes { get; }

        public void BeginRender(ICommandList commandList, IFramebuffer framebuffer, Vector4D<float> clearColor);
        public void EndRender(ICommandList commandList);
    }

    public interface ISwapchain
    {
        public IRenderTarget RenderTarget { get; }
        public IFramebuffer CurrentFramebuffer { get; }
        public bool VSync { get; set; }
        public Vector2D<int> Size { get; }

        public event Action<Vector2D<int>>? SwapchainInvalidated;

        public void Invalidate();
        public void AcquireImage();
        public void Present(ICommandQueue commandQueue, ICommandList commandList);
    }

    public interface IFramebuffer : IDisposable
    {
        public Vector2D<int> Size { get; }
    }
}
