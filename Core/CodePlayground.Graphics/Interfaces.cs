using Silk.NET.Maths;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Numerics;

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

    public enum SemaphoreUsage
    {
        Signal,
        Wait
    }

    public enum AttachmentType
    {
        Color,
        DepthStencil
    }

    public enum DeviceBufferUsage
    {
        Vertex,
        Index,
        Uniform,
        Storage,
        Staging
    }

    public enum DeviceBufferIndexType
    {
        UInt16,
        UInt32
    }

    [Flags]
    public enum DeviceImageUsageFlags
    {
        Render = 0x1,
        ColorAttachment = 0x2,
        DepthStencilAttachment = 0x4,
        CopySource = 0x8,
        CopyDestination = 0x10,
        Storage = 0x20,
    }

    public enum DeviceImageFormat
    {
        RGBA8_SRGB,
        RGBA8_UNORM,
        RGB8_SRGB,
        RGB8_UNORM,
        DepthStencil
    }

    public enum DeviceImageLayoutName
    {
        Undefined,
        ShaderReadOnly,
        ColorAttachment,
        DepthStencilAttachment,
        CopySource,
        CopyDestination,
        ComputeStorage,
    }

    public enum AddressMode
    {
        Repeat,
        MirroredRepeat,
        ClampToEdge,
        ClampToBorder,
        MirrorClampToEdge
    }

    public enum SamplerFilter
    {
        Linear,
        Nearest,
        /// <summary>
        /// Vulkan: Requires VK_EXT_filter_cubic or VK_IMG_filter_cubic
        /// </summary>
        Cubic
    }

    public enum ShaderLanguage
    {
        HLSL,
        GLSL
    }

    public enum ShaderStage
    {
        Vertex,
        Fragment,
        Geometry,
        Compute
    }

    [Flags]
    public enum ShaderResourceTypeFlags
    {
        Image = 0x1,
        Sampler = 0x2,
        UniformBuffer = 0x4,
        StorageBuffer = 0x8,
        StorageImage = 0x10,
    }

    public enum StageIODirection
    {
        In,
        Out
    }

    public enum ShaderTypeClass
    {
        Float,
        Bool,
        SInt,
        UInt,
        Image,
        Sampler,
        CombinedImageSampler,
        Struct
    }

    public enum PipelineType
    {
        Graphics,
        Compute
    }

    public enum PipelineBlendMode
    {
        None,
        Default,
        Additive,
        OneZero,
        ZeroSourceColor
    }

    public enum PipelineFrontFace
    {
        Clockwise,
        CounterClockwise
    }

    public enum MinimumDepth
    {
        Zero,
        NegativeOne
    }

    public enum MatrixType
    {
        OpenGL,
        DirectX
    }

    public struct DeviceImageInfo
    {
        public Size Size { get; set; }
        public DeviceImageUsageFlags Usage { get; set; }
        public DeviceImageFormat Format { get; set; }
        public int MipLevels { get; set; }
    }

    public struct FramebufferAttachmentInfo
    {
        public IDeviceImage Image { get; set; }
        public AttachmentType Type { get; set; }
        public object? InitialLayout { get; set; }
        public object? FinalLayout { get; set; }
        public object? Layout { get; set; }
    }

    public struct FramebufferInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public IReadOnlyList<FramebufferAttachmentInfo> Attachments { get; set; }
    }

    public struct ReflectedPushConstantBuffer
    {
        public int Type { get; set; }
        public string Name { get; set; }
    }

    public struct ReflectedShaderResource
    {
        public string Name { get; set; }
        public int Type { get; set; }
        public ShaderResourceTypeFlags ResourceType { get; set; }
    }

    public struct ReflectedStageIOField
    {
        public string Name { get; set; }
        public int Type { get; set; }
        public StageIODirection Direction { get; set; }
        public int Location { get; set; }
    }

    public struct ReflectedShaderField
    {
        public int Type { get; set; }
        public int Offset { get; set; }
        public int Stride { get; set; }
    }

    public struct ReflectedShaderType
    {
        public string Name { get; set; }
        public IReadOnlyList<int>? ArrayDimensions { get; set; }
        public ShaderTypeClass Class { get; set; }
        public IReadOnlyDictionary<string, ReflectedShaderField>? Fields { get; set; }

        public int Size { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int TotalSize => Size * Rows * Columns;
    }

    public struct ShaderReflectionResult
    {
        public List<ReflectedStageIOField> StageIO { get; set; }
        public Dictionary<int, Dictionary<int, ReflectedShaderResource>> Resources { get; set; }
        public List<ReflectedPushConstantBuffer> PushConstantBuffers { get; set; }
        public Dictionary<int, ReflectedShaderType> Types { get; set; }
    }

    public struct PipelineDescription
    {
        public IRenderTarget? RenderTarget { get; set; }
        public PipelineType Type { get; set; }
        public int FrameCount { get; set; }
        public IPipelineSpecification? Specification { get; set; }
    }

    public interface IGraphicsContext : IDisposable
    {
        public IGraphicsDeviceScorer DeviceScorer { set; }
        public IGraphicsDevice Device { get; }
        public ISwapchain? Swapchain { get; }

        public bool FlipUVs { get; }
        public bool LeftHanded { get; }
        public MinimumDepth MinDepth { get; }
        public bool ViewportFlipped { get; }
        public MatrixType MatrixType { get; }

        public bool IsApplicable(WindowOptions options);
        public void Initialize(IWindow? window, GraphicsApplication application);

        public IShaderCompiler CreateCompiler();
        public IRenderer CreateRenderer();

        public IDeviceBuffer CreateDeviceBuffer(DeviceBufferUsage usage, int size);
        public IDeviceImage CreateDeviceImage(DeviceImageInfo info);

        public IShader LoadShader(IReadOnlyList<byte> data, ShaderStage stage, string entrypoint);
        public IReflectionView CreateReflectionView(IReadOnlyDictionary<ShaderStage, IShader> shaders);
        public IPipeline CreatePipeline(PipelineDescription description);

        public IDisposable CreateSemaphore();
        public IFence CreateFence(bool signaled);

        public IFramebuffer CreateFramebuffer(FramebufferInfo info, out IRenderTarget renderTarget);
        public IFramebuffer CreateFramebuffer(FramebufferInfo info, IRenderTarget renderTarget);
    }

    public interface IGraphicsDeviceInfo
    {
        public string Name { get; }
        public DeviceType Type { get; }

        public Vector3D<uint> MaxComputeWorkGroups { get; }
    }

    public interface IGraphicsDeviceScorer
    {
        public int ScoreDevice(IGraphicsDeviceInfo device, IGraphicsContext context);
    }

    public interface IGraphicsDevice
    {
        public IGraphicsDeviceInfo DeviceInfo { get; }

        public ICommandQueue GetQueue(CommandQueueFlags usage);
        public void Wait();
        public void ClearQueues();
    }

    /// <summary>
    /// For all calls referencing ICommandList: when using Optick, make sure a GPUContextScope is initialized
    /// </summary>
    public interface ICommandList
    {
        public bool IsRecording { get; }
        public CommandQueueFlags QueueUsage { get; }
        public nint Address { get; }

        public void Begin();
        public void End();

        public void ExecutionBarrier();

        public void AddSemaphore(IDisposable semaphore, SemaphoreUsage usage);
        public void PushStagingObject(IDisposable stagingObject);
    }

    public interface ICommandQueue
    {
        public CommandQueueFlags Usage { get; }
        public int CommandListCap { get; set; }

        public ICommandList Release();
        public void Submit(ICommandList commandList, bool wait = false, IFence? fence = null);

        public void Wait();
        public void ClearCache();
        public bool ReleaseFence(IFence fence, bool wait);
    }

    public interface IFence : IDisposable
    {
        public bool IsSignaled();

        public void Reset();
        public bool Wait(ulong timeout = ulong.MaxValue);
    }

    public interface IRenderTarget : IDisposable
    {
        public IReadOnlyList<AttachmentType> AttachmentTypes { get; }
        public ulong ID { get; }

        public void BeginRender(ICommandList commandList, IFramebuffer framebuffer, Vector4 clearColor);
        public void EndRender(ICommandList commandList);
    }

    public interface IFrameSynchronizationManager
    {
        public int CurrentFrame { get; }
        public int FrameCount { get; }

        public bool ReleaseFrame(int frame, bool wait);
    }

    public interface ISwapchain : IFrameSynchronizationManager
    {
        public IRenderTarget RenderTarget { get; }
        public IFramebuffer CurrentFramebuffer { get; }

        public bool VSync { get; set; }
        public int Width { get; }
        public int Height { get; }

        public event Action<int, int>? SwapchainInvalidated;

        public void Invalidate();
        public void AcquireImage();
        public void Present(ICommandQueue commandQueue, ICommandList commandList);
    }

    public interface IFramebuffer : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
    }

    public delegate void BufferMapCallback(Span<byte> memory);
    public interface IDeviceBuffer : IDisposable
    {
        public DeviceBufferUsage Usage { get; }
        public int Size { get; }

        public unsafe void CopyFromCPU(void* address, int size, int offset = 0);
        public unsafe void CopyToCPU(void* address, int size, int offset = 0);
        public void Map(BufferMapCallback callback);

        // command list commands
        public void CopyBuffers(ICommandList commandList, IDeviceBuffer destination, int size, int srcOffset = 0, int dstOffset = 0);
        public void BindVertices(ICommandList commandList, int index);
        public void BindIndices(ICommandList commandList, DeviceBufferIndexType indexType);
    }

    public interface IDeviceImage : IDisposable
    {
        public DeviceImageUsageFlags Usage { get; }
        public Size Size { get; }
        public int MipLevels { get; }
        public DeviceImageFormat ImageFormat { get; }
        public object Layout { get; set; }

        public object GetLayout(DeviceImageLayoutName name);

        public void Load<T>(Image<T> image) where T : unmanaged, IPixel<T>;
        public void Load<T>(T[] data) where T : unmanaged;

        // command list commands
        public void CopyFromBuffer(ICommandList commandList, IDeviceBuffer source, object currentLayout);
        public void CopyToBuffer(ICommandList commandList, IDeviceBuffer destination, object currentLayout);
        public void TransitionLayout(ICommandList commandList, object srcLayout, object dstLayout);

        public ITexture CreateTexture(bool ownsImage, ISamplerSettings? samplerSettings = null);
    }

    public interface ITexture : IDisposable
    {
        public IDeviceImage Image { get; }
        public bool OwnsImage { get; }
        public ISamplerSettings? SamplerSettings { get; }
        public ulong ID { get; }

        public void InvalidateSampler();
    }

    public interface ISamplerSettings
    {
        public AddressMode AddressMode { get; }
        public SamplerFilter Filter { get; }
    }

    public interface IShaderCompiler : IDisposable
    {
        public ShaderLanguage PreferredLanguage { get; }

        public byte[] Compile(string source, string path, ShaderLanguage language, ShaderStage stage, string entrypoint);
    }

    public interface IShader : IDisposable
    {
        public ulong ID { get; }
        public ShaderStage Stage { get; }
        public string Entrypoint { get; }
        public IReadOnlyList<byte> Bytecode { get; }
        public ShaderReflectionResult ReflectionData { get; }
    }

    public interface IReflectionView
    {
        public bool ResourceExists(string resource);
        public int GetBufferSize(string resource);
        public int GetBufferOffset(string resource, string expression);
    }

    public interface IPipeline : IDisposable
    {
        public PipelineDescription Description { get; }
        public IReflectionView ReflectionView { get; }
        public ulong ID { get; }

        public void Bind(ICommandList commandList, int frame);
        public void Bind(ICommandList commandList, nint id);
        public void PushConstants(ICommandList commandList, BufferMapCallback callback);

        // it is the implementation's responsibility to avoid duplicate bindings
        public bool Bind(IDeviceBuffer buffer, string name, int index = 0);
        public bool Bind(IDeviceImage image, string name, int index = 0);
        public bool Bind(ITexture texture, string name, int index = 0);

        public nint CreateDynamicID(IDeviceBuffer buffer, string name, int index = 0);
        public nint CreateDynamicID(ITexture texture, string name, int index = 0);
        public void UpdateDynamicID(nint id);
        public void DestroyDynamicID(nint id);

        public void Load(IReadOnlyDictionary<ShaderStage, IShader> shaders);
    }

    public interface IPipelineSpecification
    {
        public PipelineBlendMode BlendMode { get; }
        public PipelineFrontFace FrontFace { get; }
        public bool EnableDepthTesting { get; }
        public bool DisableCulling { get; }
    }

    public interface IRenderer
    {
        public void SetScissor(ICommandList commandList, int index, int x, int y, int width, int height);
        public void RenderIndexed(ICommandList commandList, int indexOffset, int indexCount);

        public void DispatchCompute(ICommandList commandList, int x, int y, int z);
    }
}
