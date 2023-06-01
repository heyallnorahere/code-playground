using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Vulkan;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using VulkanTest.Shaders;

[assembly: LoadedApplication(typeof(VulkanTest.VulkanTestApp))]

namespace VulkanTest
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3D<float> Position;
        public Vector3D<float> Color;
    }

    [ApplicationTitle("Vulkan Test")]
    [ApplicationGraphicsAPI(AppGraphicsAPI.Vulkan)]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    public class VulkanTestApp : GraphicsApplication
    {
        private static readonly Vertex[] sVertices;
        private static readonly uint[] sIndices;

        static VulkanTestApp()
        {
            sVertices = new Vertex[]
            {
                new Vertex
                {
                    Position = new Vector3D<float>(-0.5f, -0.5f, 0f),
                    Color = new Vector3D<float>(1f, 0f, 0f)
                },
                new Vertex
                {
                    Position = new Vector3D<float>(0.5f, -0.5f, 0f),
                    Color = new Vector3D<float>(0f, 1f, 0f)
                },
                new Vertex
                {
                    Position = new Vector3D<float>(0f, 0.5f, 0f),
                    Color = new Vector3D<float>(0f, 0f, 1f)
                }
            };

            sIndices = new uint[]
            {
                0, 2, 1
            };
        }

        public VulkanTestApp()
        {
            Utilities.BindHandlers(this, this);
        }

        [EventHandler(nameof(Load))]
        private void OnLoad()
        {
            CreateGraphicsContext<VulkanContext>();

            var context = GraphicsContext!;
            var swapchain = context.Swapchain;
            swapchain.VSync = true; // enable vsync

            mShaderLibrary = new ShaderLibrary(this);
            mRenderer = context.CreateRenderer();

            mPipeline = mShaderLibrary.LoadPipeline<TestShader>(new PipelineDescription
            {
                RenderTarget = swapchain.RenderTarget,
                Type = PipelineType.Graphics,
                FrameCount = swapchain.FrameCount
            });

            int vertexBufferSize = sVertices.Length * Marshal.SizeOf<Vertex>();
            int indexBufferSize = sIndices.Length * Marshal.SizeOf<uint>();

            using var vertexStagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, vertexBufferSize);
            using var indexStagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, indexBufferSize);

            mVertexBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Vertex, vertexBufferSize);
            mIndexBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Index, indexBufferSize);

            vertexStagingBuffer.CopyFromCPU(sVertices);
            indexStagingBuffer.CopyFromCPU(sIndices);

            var transferQueue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = transferQueue.Release();

            commandList.Begin();
            vertexStagingBuffer.CopyBuffers(commandList, mVertexBuffer, vertexBufferSize);
            indexStagingBuffer.CopyBuffers(commandList, mIndexBuffer, indexBufferSize);
            commandList.End();

            transferQueue.Submit(commandList, true);
        }

        protected override void OnContextCreation(IGraphicsContext context)
        {
            if (context is VulkanContext vulkanContext)
            {
                vulkanContext.DebugMessage += DebugMessageCallback;
            }
        }

        private unsafe static void DebugMessageCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type, DebugUtilsMessengerCallbackDataEXT data)
        {
            var severityNames = new Dictionary<DebugUtilsMessageSeverityFlagsEXT, string>
            {
                [DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt] = "Verbose",
                [DebugUtilsMessageSeverityFlagsEXT.InfoBitExt] = "Info",
                [DebugUtilsMessageSeverityFlagsEXT.WarningBitExt] = "Warning",
                [DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt] = "Error"
            };

            string severityName = string.Empty;
            foreach (var severityFlag in severityNames.Keys)
            {
                if (severity.HasFlag(severityFlag))
                {
                    severityName = severityNames[severityFlag];
                    break;
                }
            }

            string message = Marshal.PtrToStringAnsi((nint)data.PMessage) ?? string.Empty;
            Console.WriteLine($"Vulkan validation layer: [{severityName}] {message}");

            if (Debugger.IsAttached && severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt))
            {
                Debugger.Break();
            }
        }

        [EventHandler(nameof(Closing))]
        private void OnClose()
        {
            var device = GraphicsContext?.Device;
            device?.ClearQueues();

            mVertexBuffer?.Dispose();
            mIndexBuffer?.Dispose();
            mPipeline?.Dispose();

            mShaderLibrary?.Dispose();
            GraphicsContext?.Dispose();
        }

        [EventHandler(nameof(Render))]
        private void OnRender(FrameRenderInfo renderInfo)
        {
            if (renderInfo.CommandList is null || renderInfo.RenderTarget is null || renderInfo.Framebuffer is null)
            {
                return;
            }

            var clearColor = new Vector4D<float>(0f, 0f, 0f, 1f);
            renderInfo.RenderTarget.BeginRender(renderInfo.CommandList, renderInfo.Framebuffer, clearColor, true);

            mVertexBuffer!.BindVertices(renderInfo.CommandList, 0);
            mIndexBuffer!.BindIndices(renderInfo.CommandList, DeviceBufferIndexType.UInt32);
            mPipeline!.Bind(renderInfo.CommandList, renderInfo.CurrentFrame);
            mRenderer!.RenderIndexed(renderInfo.CommandList, sIndices.Length);

            renderInfo.RenderTarget.EndRender(renderInfo.CommandList);
        }

        private ShaderLibrary? mShaderLibrary;
        private IPipeline? mPipeline;
        private IDeviceBuffer? mVertexBuffer, mIndexBuffer;
        private IRenderer? mRenderer;
    }
}
