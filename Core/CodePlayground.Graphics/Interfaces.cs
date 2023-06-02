using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

    public enum DeviceBufferUsage
    {
        Vertex,
        Index,
        Uniform,
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
        CopyDestination
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
        StorageBuffer = 0x8
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
        SourceAlphaOneMinusSourceAlpha,
        OneZero,
        ZeroSourceColor
    }

    public enum PipelineFrontFace
    {
        Clockwise,
        CounterClockwise
    }

    public struct DeviceImageInfo
    {
        public Size Size { get; set; }
        public DeviceImageUsageFlags Usage { get; set; }
        public DeviceImageFormat Format { get; set; }
        public int MipLevels { get; set; }
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
        public ISwapchain Swapchain { get; }

        public bool IsApplicable(WindowOptions options);
        public void Initialize(IWindow window, GraphicsApplication application);

        public IShaderCompiler CreateCompiler();
        public IRenderer CreateRenderer();

        public IDeviceBuffer CreateDeviceBuffer(DeviceBufferUsage usage, int size);
        public IDeviceImage CreateDeviceImage(DeviceImageInfo info);
        public IShader LoadShader(IReadOnlyList<byte> data, ShaderStage stage, string entrypoint);
        public IPipeline CreatePipeline(PipelineDescription description);
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
        public void Wait();
        public void ClearQueues();
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
        public void Submit(ICommandList commandList, bool wait = false);

        public void Wait();
        public void ClearCache();
    }

    public interface IRenderTarget : IDisposable
    {
        public IReadOnlyList<AttachmentType> AttachmentTypes { get; }

        public void BeginRender(ICommandList commandList, IFramebuffer framebuffer, Vector4D<float> clearColor, bool flipViewport = false);
        public void EndRender(ICommandList commandList);
    }

    public interface ISwapchain
    {
        public IRenderTarget RenderTarget { get; }
        public IFramebuffer CurrentFramebuffer { get; }
        public int CurrentFrame { get; }
        public int FrameCount { get; }

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

    public interface IDeviceBuffer : IDisposable
    {
        public DeviceBufferUsage Usage { get; }
        public int Size { get; }

        public unsafe void CopyFromCPU(void* address, int size);
        public unsafe void CopyToCPU(void* address, int size);

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
    }

    public interface IShaderCompiler : IDisposable
    {
        public ShaderLanguage PreferredLanguage { get; }

        public byte[] Compile(string source, string path, ShaderLanguage language, ShaderStage stage, string entrypoint);
        public ShaderReflectionResult Reflect(IReadOnlyList<byte> bytecode);
    }

    public interface IShader : IDisposable
    {
        public ShaderStage Stage { get; }
        public string Entrypoint { get; }
        public IReadOnlyList<byte> Bytecode { get; }
    }

    public interface IPipeline : IDisposable
    {
        public PipelineDescription Description { get; }

        public void Bind(ICommandList commandList, int frame);
        public bool Bind(IDeviceBuffer buffer, string name, int index = 0);
        // todo: texture binding

        public void Load(IReadOnlyDictionary<ShaderStage, IShader> shaders);
    }

    public interface IPipelineSpecification
    {
        public PipelineBlendMode BlendMode { get; }
        public PipelineFrontFace FrontFace { get; }
        public bool EnableDepthTesting { get; }
    }

    public interface IRenderer
    {
        public void RenderIndexed(ICommandList commandBuffer, int indexCount);
    }
}
