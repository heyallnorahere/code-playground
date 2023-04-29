using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Vulkan;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

[assembly: LoadedApplication(typeof(VulkanTest.VulkanTestApp))]

namespace VulkanTest
{
    [ApplicationTitle("Vulkan Test")]
    [ApplicationGraphicsAPI(AppGraphicsAPI.Vulkan)]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    public class VulkanTestApp : GraphicsApplication
    {
        public VulkanTestApp()
        {
            Utilities.BindHandlers(this, this);
        }

        [EventHandler(nameof(Load))]
        private unsafe void OnLoad()
        {
            mContext = CreateGraphicsContext<VulkanContext>();

            var device = mContext.Device;
            var queue = device.GetQueue(CommandQueueFlags.Transfer);

            var buffer = queue.Release();
            buffer.Begin();

            buffer.End();
            queue.Submit(buffer);
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
            mContext?.Dispose();
        }

        private VulkanContext? mContext;
    }
}
