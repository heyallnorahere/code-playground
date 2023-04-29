using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanPhysicalDevice : IGraphicsDeviceInfo
    {
        internal unsafe VulkanPhysicalDevice(PhysicalDevice physicalDevice)
        {
            Device = physicalDevice;
            GetProperties(out PhysicalDeviceProperties properties);

            Name = Marshal.PtrToStringAnsi((nint)properties.DeviceName) ?? string.Empty;
            Type = properties.DeviceType switch
            {
                PhysicalDeviceType.Other => DeviceType.Other,
                PhysicalDeviceType.IntegratedGpu => DeviceType.Integrated,
                PhysicalDeviceType.DiscreteGpu => DeviceType.Discrete,
                PhysicalDeviceType.VirtualGpu => DeviceType.Virtual,
                _ => throw new NotImplementedException()
            };
        }

        public void GetProperties(out PhysicalDeviceProperties properties)
        {
            var api = VulkanContext.API;
            properties = api.GetPhysicalDeviceProperties(Device);
        }

        public void GetFeatures(out PhysicalDeviceFeatures features)
        {
            var api = VulkanContext.API;
            features = api.GetPhysicalDeviceFeatures(Device);
        }

        public unsafe IReadOnlyList<QueueFamilyProperties> GetQueueFamilyProperties()
        {
            var api = VulkanContext.API;

            uint queueFamilyCount = 0;
            api.GetPhysicalDeviceQueueFamilyProperties(Device, &queueFamilyCount, null);

            var properties = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* ptr = properties)
            {
                api.GetPhysicalDeviceQueueFamilyProperties(Device, &queueFamilyCount, ptr);
            }

            return properties;
        }

        public IReadOnlyDictionary<GraphicsQueueFlags, int> FindQueueTypes()
        {
            int queueCount = Enum.GetValues<GraphicsQueueFlags>().Length;
            var queueFamilyProperties = GetQueueFamilyProperties();

            var result = new Dictionary<GraphicsQueueFlags, int>();
            for (int i = 0; i < queueFamilyProperties.Count; i++)
            {
                var properties = queueFamilyProperties[i];
                var flagValues = properties.QueueFlags.SplitFlags();

                foreach (var flagValue in flagValues)
                {
                    GraphicsQueueFlags value = flagValue switch
                    {
                        QueueFlags.GraphicsBit => GraphicsQueueFlags.Graphics,
                        QueueFlags.ComputeBit => GraphicsQueueFlags.Compute,
                        QueueFlags.TransferBit => GraphicsQueueFlags.Transfer,
                        _ => 0
                    };

                    if (value == 0 || result.ContainsKey(value))
                    {
                        continue;
                    }

                    result.Add(value, i);
                    if (result.Count == queueCount)
                    {
                        break;
                    }
                }

                if (result.Count == queueCount)
                {
                    break;
                }
            }

            return result;
        }

        public PhysicalDevice Device { get; }
        public string Name { get; }
        public DeviceType Type { get; }
    }

    internal struct VulkanDeviceCreateInfo
    {
        public VulkanPhysicalDevice Device { get; set; }
        public IEnumerable<string> Extensions { get; set; }
        public IEnumerable<string> Layers { get; set; }
        public IEnumerable<int> AdditionalQueueFamilies { get; set; }
    }

    public sealed class VulkanDevice : IGraphicsDevice, IDisposable
    {
        internal unsafe VulkanDevice(VulkanDeviceCreateInfo info)
        {
            mPhysicalDevice = info.Device;
            mDisposed = false;

            var api = VulkanContext.API;
            var queueTypes = mPhysicalDevice.FindQueueTypes();
            var queueProperties = mPhysicalDevice.GetQueueFamilyProperties();
            var queueFamilies = queueTypes.Values.Concat(info.AdditionalQueueFamilies).ToHashSet().ToArray();
            var queueInfo = new DeviceQueueCreateInfo[queueFamilies.Length];

            float priority = 1f;
            for (int i = 0; i < queueFamilies.Length; i++)
            {
                queueInfo[i] = VulkanUtilities.Init<DeviceQueueCreateInfo>() with
                {
                    QueueCount = 1,
                    QueueFamilyIndex = (uint)queueFamilies[i],
                    PQueuePriorities = &priority
                };
            }

            using var marshal = new StringMarshal();
            info.Extensions.CreateNativeStringArray(extensions =>
            {
                info.Layers.CreateNativeStringArray(layers =>
                {
                    fixed (DeviceQueueCreateInfo* queuePtr = queueInfo)
                    {
                        var createInfo = VulkanUtilities.Init<DeviceCreateInfo>() with
                        {
                            QueueCreateInfoCount = (uint)queueInfo.Length,
                            PQueueCreateInfos = queuePtr,
                            EnabledLayerCount = (uint)info.Layers.Count(),
                            PpEnabledLayerNames = layers,
                            EnabledExtensionCount = (uint)info.Extensions.Count(),
                            PpEnabledExtensionNames = extensions,
                        };

                        fixed (Device* devicePtr = &mDevice)
                        {
                            api.CreateDevice(mPhysicalDevice.Device, &createInfo, null, devicePtr).Assert();
                        }
                    }
                }, marshal);
            }, marshal);

            // todo: create graphics queues
        }

        ~VulkanDevice()
        {
            if (!mDisposed)
            {
                Dispose(false);
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

        private unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                // todo: dispose queues
            }

            var api = VulkanContext.API;
            api.DestroyDevice(mDevice, null);
        }

        public IGraphicsQueue GetQueue(GraphicsQueueFlags flags)
        {
            // todo: implement
            throw new NotImplementedException();
        }

        public VulkanPhysicalDevice PhysicalDevice => mPhysicalDevice;
        IGraphicsDeviceInfo IGraphicsDevice.DeviceInfo => throw new NotImplementedException();

        private readonly VulkanPhysicalDevice mPhysicalDevice;
        private Device mDevice;
        private bool mDisposed;
    }
}
