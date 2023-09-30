using Silk.NET.Windowing;
using System;
using System.Collections.Generic;

namespace CodePlayground.Graphics.Metal
{
    public sealed class MetalContext : IGraphicsContext
    {
        public IGraphicsDeviceScorer DeviceScorer { set => throw new NotImplementedException(); }

        public IGraphicsDevice Device => throw new NotImplementedException();

        public ISwapchain? Swapchain => throw new NotImplementedException();

        public bool FlipUVs => throw new NotImplementedException();

        public bool LeftHanded => throw new NotImplementedException();

        public MinimumDepth MinDepth => throw new NotImplementedException();

        public bool ViewportFlipped => throw new NotImplementedException();

        public MatrixType MatrixType => throw new NotImplementedException();

        public IShaderCompiler CreateCompiler()
        {
            throw new NotImplementedException();
        }

        public IDeviceBuffer CreateDeviceBuffer(DeviceBufferUsage usage, int size)
        {
            throw new NotImplementedException();
        }

        public IDeviceImage CreateDeviceImage(DeviceImageInfo info)
        {
            throw new NotImplementedException();
        }

        public IFence CreateFence(bool signaled)
        {
            throw new NotImplementedException();
        }

        public IFramebuffer CreateFramebuffer(FramebufferInfo info, out IRenderTarget renderTarget)
        {
            throw new NotImplementedException();
        }

        public IFramebuffer CreateFramebuffer(FramebufferInfo info, IRenderTarget renderTarget)
        {
            throw new NotImplementedException();
        }

        public IPipeline CreatePipeline(PipelineDescription description)
        {
            throw new NotImplementedException();
        }

        public IReflectionView CreateReflectionView(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            throw new NotImplementedException();
        }

        public IRenderer CreateRenderer()
        {
            throw new NotImplementedException();
        }

        public IDisposable CreateSemaphore()
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void Initialize(IWindow? window, GraphicsApplication application)
        {
            throw new NotImplementedException();
        }

        public bool IsApplicable(WindowOptions options)
        {
            throw new NotImplementedException();
        }

        public IShader LoadShader(IReadOnlyList<byte> data, ShaderStage stage, string entrypoint)
        {
            throw new NotImplementedException();
        }
    }
}
