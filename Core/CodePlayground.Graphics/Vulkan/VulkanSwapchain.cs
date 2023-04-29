using Silk.NET.Core;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics.Vulkan
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal unsafe delegate void PFN_vkDestroySurfaceKHR(Instance instance, SurfaceKHR surface, AllocationCallbacks* pAllocator);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal unsafe delegate Result PFN_vkGetPhysicalDeviceSurfaceSupportKHR(PhysicalDevice physicalDevice, uint queueFamilyIndex, SurfaceKHR surface, Bool32* pSupported);

    public sealed class VulkanSwapchain : ISwapchain, IDisposable
    {
        public static unsafe int FindPresentQueueFamily(VulkanPhysicalDevice physicalDevice, Instance instance, SurfaceKHR surface)
        {
            var queueFamilies = physicalDevice.GetQueueFamilyProperties();

            var api = VulkanContext.API;
            var vkGetPhysicalDeviceSurfaceSupportKHR = api.GetProcAddress<PFN_vkGetPhysicalDeviceSurfaceSupportKHR>(instance);

            if (vkGetPhysicalDeviceSurfaceSupportKHR is null)
            {
                throw new MissingMethodException($"Failed to find Vulkan function {nameof(vkGetPhysicalDeviceSurfaceSupportKHR)}!");
            }

            for (int i = 0; i < queueFamilies.Count; i++)
            {
                Bool32 supported = false;
                vkGetPhysicalDeviceSurfaceSupportKHR(physicalDevice.Device, (uint)i, surface, &supported).Assert();

                if (supported)
                {
                    return i;
                }
            }

            throw new ArgumentException("Failed to find present queue family!");
        }

        public VulkanSwapchain()
        {
            mDisposed = false;

            // todo: create swapchain
        }

        ~VulkanSwapchain()
        {
            if (!mDisposed)
            {
                Dispose(false);
                mDisposed = true;
            }
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Dispose(true);
            mDisposed = true;
        }

        private void Dispose(bool disposing)
        {
            // todo: destroy surface/swapchain
        }

        private bool mDisposed;
    }
}
