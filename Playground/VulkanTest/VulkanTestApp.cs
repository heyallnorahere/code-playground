using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Vulkan;
using System.Diagnostics.CodeAnalysis;

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
        private void OnLoad()
        {
            mContext = CreateGraphicsContext<VulkanContext>();
        }

        [EventHandler(nameof(Closing))]
        private void OnClose()
        {
            mContext?.Dispose();
        }

        private VulkanContext? mContext;
    }
}
