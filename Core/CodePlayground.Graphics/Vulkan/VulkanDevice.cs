using Optick.NET;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanPhysicalDevice : IGraphicsDeviceInfo
    {
        internal unsafe VulkanPhysicalDevice(PhysicalDevice physicalDevice, Instance instance)
        {
            Device = physicalDevice;
            Instance = instance;
            GetProperties(out PhysicalDeviceProperties properties);

            Name = Marshal.PtrToStringAnsi((nint)properties.DeviceName) ?? string.Empty;
            Type = properties.DeviceType switch
            {
                PhysicalDeviceType.Other => DeviceType.Other,
                PhysicalDeviceType.IntegratedGpu => DeviceType.Integrated,
                PhysicalDeviceType.DiscreteGpu => DeviceType.Discrete,
                PhysicalDeviceType.VirtualGpu => DeviceType.Virtual,
                PhysicalDeviceType.Cpu => DeviceType.CPU,
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

        public void GetFormatProperties(Format format, out FormatProperties properties)
        {
            var api = VulkanContext.API;
            api.GetPhysicalDeviceFormatProperties(Device, format, out properties);
        }

        public bool FindSupportedFormat(IEnumerable<Format> candidates, ImageTiling tiling, FormatFeatureFlags requiredFeatures, out Format format)
        {
            foreach (var currentFormat in candidates)
            {
                GetFormatProperties(currentFormat, out FormatProperties properties);

                var featureFlags = tiling switch
                {
                    ImageTiling.Linear => properties.LinearTilingFeatures,
                    ImageTiling.Optimal => properties.OptimalTilingFeatures,
                    _ => throw new ArgumentException("Invalid image tiling!")
                };

                if (featureFlags.HasFlag(requiredFeatures))
                {
                    format = currentFormat;
                    return true;
                }
            }

            format = Format.Undefined;
            return false;
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

        public IReadOnlyDictionary<CommandQueueFlags, int> FindQueueTypes()
        {
            int queueCount = Enum.GetValues<CommandQueueFlags>().Length;
            var queueFamilyProperties = GetQueueFamilyProperties();

            var result = new Dictionary<CommandQueueFlags, int>();
            for (int i = 0; i < queueFamilyProperties.Count; i++)
            {
                var properties = queueFamilyProperties[i];
                var flagValues = VulkanUtilities.ConvertQueueFlags(properties.QueueFlags).SplitFlags();

                foreach (var flagValue in flagValues)
                {
                    if (result.ContainsKey(flagValue))
                    {
                        continue;
                    }

                    result.Add(flagValue, i);
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

        public unsafe Vector3D<uint> MaxComputeWorkGroups
        {
            get
            {
                GetProperties(out PhysicalDeviceProperties properties);

                var result = new Vector3D<uint>();
                for (int i = 0; i < 3; i++)
                {
                    uint count = properties.Limits.MaxComputeWorkGroupCount[i];

                    // silk.net you suck
                    switch (i)
                    {
                        case 0:
                            result.X = count;
                            break;
                        case 1:
                            result.Y = count;
                            break;
                        case 2:
                            result.Z = count;
                            break;
                    }
                }

                return result;
            }
        }

        public PhysicalDevice Device { get; }
        public Instance Instance { get; }
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
                        PhysicalDevice.GetFeatures(out PhysicalDeviceFeatures features);
                        var createInfo = VulkanUtilities.Init<DeviceCreateInfo>() with
                        {
                            QueueCreateInfoCount = (uint)queueInfo.Length,
                            PQueueCreateInfos = queuePtr,
                            EnabledLayerCount = (uint)info.Layers.Count(),
                            PpEnabledLayerNames = layers,
                            EnabledExtensionCount = (uint)info.Extensions.Count(),
                            PpEnabledExtensionNames = extensions,
                            PEnabledFeatures = &features
                        };

                        fixed (Device* devicePtr = &mDevice)
                        {
                            api.CreateDevice(mPhysicalDevice.Device, &createInfo, null, devicePtr).Assert();
                        }
                    }
                }, marshal);
            }, marshal);

            mQueues = new Dictionary<int, VulkanQueue>();
            for (int i = 0; i < queueFamilies.Length; i++)
            {
                int queueFamily = queueFamilies[i];

                var properties = queueProperties[queueFamily];
                var usage = VulkanUtilities.ConvertQueueFlags(properties.QueueFlags);

                var queue = new VulkanQueue(queueFamily, usage, this);
                mQueues.Add(queueFamily, queue);
            }

            if (queueTypes.TryGetValue(CommandQueueFlags.Transfer, out int graphicsFamily))
            {
                var graphicsQueue = mQueues[graphicsFamily].Queue.Handle;
                InitializeOptick(graphicsQueue, (uint)graphicsFamily);
            }
        }

        private unsafe void InitializeOptick(nint graphicsQueue, uint graphicsFamily)
        {
            var instanceFunctions = new HashSet<string>
            {
                nameof(VulkanFunctions.vkGetPhysicalDeviceProperties)
            };

            object vulkanFunctions = new VulkanFunctions();
            var functionType = vulkanFunctions.GetType();
            var fields = functionType.GetFields(BindingFlags.Public | BindingFlags.Instance);

            var api = VulkanContext.API;
            foreach (var field in fields)
            {
                nint functionAddress;
                string functionName = field.Name;

                if (instanceFunctions.Contains(functionName))
                {
                    functionAddress = api.GetInstanceProcAddr(mPhysicalDevice.Instance, functionName);
                }
                else
                {
                    functionAddress = api.GetDeviceProcAddr(mDevice, functionName);
                }

                field.SetValue(vulkanFunctions, functionAddress);
            }

            nint device = mDevice.Handle;
            nint physicalDevice = mPhysicalDevice.Device.Handle;

            var vulkanFunctionStructure = (VulkanFunctions)vulkanFunctions;
            OptickImports.InitGpuVulkan(&device, &physicalDevice, &graphicsQueue, &graphicsFamily, 1, &vulkanFunctionStructure);
        }

        ~VulkanDevice()
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

        private unsafe void Dispose(bool disposing)
        {
            foreach (var queue in mQueues.Values)
            {
                queue.Dispose();
            }

            var api = VulkanContext.API;
            api.DestroyDevice(mDevice, null);
        }

        ICommandQueue IGraphicsDevice.GetQueue(CommandQueueFlags usage) => GetQueue(usage);
        public VulkanQueue GetQueue(CommandQueueFlags flags)
        {
            foreach (var queue in mQueues.Values)
            {
                if ((queue.Usage & flags) == flags)
                {
                    return queue;
                }
            }

            throw new ArgumentException("No suitable queue found!");
        }

        public VulkanQueue GetQueue(int queueFamily)
        {
            if (!mQueues.ContainsKey(queueFamily))
            {
                throw new ArgumentException($"No queue was created for family {queueFamily}");
            }

            return mQueues[queueFamily];
        }

        public void Wait()
        {
            var api = VulkanContext.API;
            api.DeviceWaitIdle(mDevice).Assert();
        }

        public void ClearQueues()
        {
            Wait();
            foreach (var queue in mQueues.Values)
            {
                queue.ClearCache();
            }
        }

        public VulkanPhysicalDevice PhysicalDevice => mPhysicalDevice;
        public Device Device => mDevice;
        IGraphicsDeviceInfo IGraphicsDevice.DeviceInfo => mPhysicalDevice;

        private readonly VulkanPhysicalDevice mPhysicalDevice;
        private readonly Dictionary<int, VulkanQueue> mQueues;
        private Device mDevice;
        private bool mDisposed;
    }
}
