using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics.Vulkan
{
    public enum VulkanExtensionLevel
    {
        Instance,
        Device,
    }

    public enum VulkanExtensionType
    {
        Extension,
        Layer
    }

    internal struct RequestedVulkanExtensionDesc
    {
        public string Name { get; set; }
        public VulkanExtensionLevel Level { get; set; }
        public VulkanExtensionType Type { get; set; }
        public bool Required { get; set; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal unsafe delegate Result PFN_vkCreateDebugUtilsMessengerEXT(Instance instance, DebugUtilsMessengerCreateInfoEXT* createInfo, AllocationCallbacks* allocator, DebugUtilsMessengerEXT* messenger);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal unsafe delegate void PFN_vkDestroyDebugUtilsMessengerEXT(Instance instance, DebugUtilsMessengerEXT messenger, AllocationCallbacks* callbacks);

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class RequestedVulkanExtensionAttribute : Attribute
    {
        public RequestedVulkanExtensionAttribute(string name, VulkanExtensionLevel level, VulkanExtensionType type)
        {
            Name = name;
            Level = level;
            Type = type;
            Required = true;
        }

        public string Name { get; }
        public VulkanExtensionLevel Level { get; }
        public VulkanExtensionType Type { get; }
        public bool Required { get; set; }
    }

    internal sealed class DefaultVulkanDeviceScorer : IGraphicsDeviceScorer
    {
        private static bool CompareExtensions(VulkanPhysicalDevice device, VulkanContext context, VulkanExtensionType type, out int totalPresent)
        {
            var requested = context.GetRequestedExtensions(VulkanExtensionLevel.Device, type);
            var available = context.GetAvailableExtensions(device, type).ToHashSet();

            totalPresent = 0;
            bool viable = true;

            foreach (string name in requested.Keys)
            {
                if (available.Contains(name))
                {
                    totalPresent++;
                }
                else if (requested[name])
                {
                    viable = false;
                }
            }

            return viable;
        }

        public int ScoreDevice(IGraphicsDeviceInfo device, IGraphicsContext context)
        {
            if (device is not VulkanPhysicalDevice || context is not VulkanContext)
            {
                throw new ArgumentException("Vulkan objects must be passed!");
            }

            var physicalDevice = (VulkanPhysicalDevice)device;
            var vulkanContext = (VulkanContext)context;

            int score = physicalDevice.Type switch
            {
                DeviceType.Discrete => 5000,
                DeviceType.Integrated => 3000,
                _ => 0,
            };

            var extensionTypes = Enum.GetValues<VulkanExtensionType>();
            foreach (var extensionType in extensionTypes)
            {
                if (!CompareExtensions(physicalDevice, vulkanContext, extensionType, out int totalPresent))
                {
                    return 0;
                }

                score += totalPresent * 1000;
            }

            physicalDevice.GetProperties(out PhysicalDeviceProperties properties);
            physicalDevice.GetFeatures(out PhysicalDeviceFeatures features);
            
            // todo: change score based off of features/properties

            return score;
        }
    }

    internal struct ScoredVulkanDevice
    {
        public VulkanPhysicalDevice Device { get; set; }
        public int Score { get; set; }
    }

    public sealed class VulkanContext : IGraphicsContext
    {
        public static Vk API { get; }
        static VulkanContext()
        {
            API = Vk.GetApi();
        }

        public VulkanContext(IGraphicsDeviceScorer? deviceScorer = null)
        {
            mInstance = null;
            mDebugMessenger = null;

            mInitialized = false;
            mRequestedExtensions = new Dictionary<VulkanExtensionLevel, Dictionary<VulkanExtensionType, Dictionary<string, bool>>>();

            if (deviceScorer is null)
            {
                mDeviceScorer = new DefaultVulkanDeviceScorer();
            }
            else
            {
                mDeviceScorer = deviceScorer;
            }
        }

        ~VulkanContext()
        {
            if (mInitialized)
            {
                Dispose(false);
            }
        }

        public bool IsApplicable(WindowOptions options)
        {
            return options.API.Equals(GraphicsAPI.DefaultVulkan);
        }

        public void Initialize(IWindow window, GraphicsApplication application)
        {
            if (mInitialized)
            {
                throw new InvalidOperationException("Context has already been initialized!");
            }

            RetrieveRequestedExtensionsAndLayers(application);
            CreateInstance(application.Title, application.Version);
            CreateDebugMessenger();
            CreateDevice();

            mInitialized = true;
        }

        private static unsafe IEnumerable<string> GetWindowExtensions(GraphicsApplication application)
        {
            var surfaceFactory = application.VulkanSurfaceFactory;
            if (surfaceFactory is null)
            {
                return Array.Empty<string>();
            }

            var extensionArray = surfaceFactory.GetRequiredExtensions(out uint extensionCount);
            var result = new List<string>();

            for (uint i = 0; i < extensionCount; i++)
            {
                var name = Marshal.PtrToStringAnsi((nint)extensionArray[i]);
                result.Add(name!);
            }

            return result;
        }

        private void RetrieveRequestedExtensionsAndLayers(GraphicsApplication application)
        {
            var type = application.GetType();
            var attributes = type.GetCustomAttributes<RequestedVulkanExtensionAttribute>();
            var windowExtensions = GetWindowExtensions(application);

            var attributeExtDescs = attributes.Select(attribute => new RequestedVulkanExtensionDesc
            {
                Name = attribute.Name,
                Level = attribute.Level,
                Type = attribute.Type,
                Required = attribute.Required
            });

            var windowExtDescs = windowExtensions.Select(name => new RequestedVulkanExtensionDesc
            {
                Name = name,
                Level = VulkanExtensionLevel.Instance,
                Type = VulkanExtensionType.Extension,
                Required = true
            });

            var extensions = attributeExtDescs.Concat(windowExtDescs);
            foreach (var extension in extensions)
            {
                if (!mRequestedExtensions.ContainsKey(extension.Level))
                {
                    mRequestedExtensions.Add(extension.Level, new Dictionary<VulkanExtensionType, Dictionary<string, bool>>());
                }

                var typeDict = mRequestedExtensions[extension.Level];
                if (!typeDict.ContainsKey(extension.Type))
                {
                    typeDict.Add(extension.Type, new Dictionary<string, bool>());
                }

                var extensionDict = typeDict[extension.Type];
                if (!extensionDict.ContainsKey(extension.Name))
                {
                    extensionDict.Add(extension.Name, extension.Required);
                }
                else if (!extensionDict[extension.Name])
                {
                    extensionDict[extension.Name] = extension.Required;
                }
            }
        }

        public IReadOnlyDictionary<string, bool> GetRequestedExtensions(VulkanExtensionLevel level, VulkanExtensionType type)
        {
            return mRequestedExtensions.GetValueOrDefault(level)?.GetValueOrDefault(type) ?? new Dictionary<string, bool>();
        }

        public unsafe IEnumerable<string> GetAvailableExtensions(VulkanPhysicalDevice? device, VulkanExtensionType type)
        {
            switch (type)
            {
                case VulkanExtensionType.Extension:
                    if (device is null)
                    {
                        uint extensionCount = 0;
                        API.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null).Assert();

                        var extensions = new ExtensionProperties[extensionCount];
                        fixed (ExtensionProperties* ptr = extensions)
                        {
                            API.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, ptr).Assert();
                        }

                        return extensions.Select(ext => Marshal.PtrToStringAnsi((nint)ext.ExtensionName)!);
                    }
                    else
                    {
                        uint extensionCount = 0;
                        API.EnumerateDeviceExtensionProperties(device.Device, (byte*)null, &extensionCount, null).Assert();

                        var extensions = new ExtensionProperties[extensionCount];
                        fixed (ExtensionProperties* ptr = extensions)
                        {
                            API.EnumerateDeviceExtensionProperties(device.Device, (byte*)null, &extensionCount, ptr).Assert();
                        }

                        return extensions.Select(ext => Marshal.PtrToStringAnsi((nint)ext.ExtensionName)!);
                    }
                case VulkanExtensionType.Layer:
                    if (device is null)
                    {
                        uint layerCount = 0;
                        API.EnumerateInstanceLayerProperties(&layerCount, null).Assert();

                        var layers = new LayerProperties[layerCount];
                        fixed (LayerProperties* ptr = layers)
                        {
                            API.EnumerateInstanceLayerProperties(&layerCount, ptr).Assert();
                        }

                        return layers.Select(layer => Marshal.PtrToStringAnsi((nint)layer.LayerName)!);
                    }
                    else
                    {
                        uint layerCount = 0;
                        API.EnumerateDeviceLayerProperties(device.Device, &layerCount, null).Assert();

                        var layers = new LayerProperties[layerCount];
                        fixed (LayerProperties* ptr = layers)
                        {
                            API.EnumerateDeviceLayerProperties(device.Device, &layerCount, ptr).Assert();
                        }

                        return layers.Select(layer => Marshal.PtrToStringAnsi((nint)layer.LayerName)!);
                    }
            }

            // this point should not be hit
            throw new NotImplementedException();
        }

        private IReadOnlyList<string> ChooseExtensions(VulkanPhysicalDevice? device, VulkanExtensionType type)
        {
            VulkanExtensionLevel level;
            if (device is null)
            {
                level = VulkanExtensionLevel.Instance;
            }
            else
            {
                level = VulkanExtensionLevel.Device;
            }

            var requested = GetRequestedExtensions(level, type);
            var available = GetAvailableExtensions(device, type).ToHashSet();

            var result = new List<string>();
            string extensionType = $"{level.ToString().ToLower()} {type.ToString().ToLower()}";
            foreach (var extension in requested.Keys)
            {
                Console.WriteLine($"Using Vulkan {extensionType}: {extension}");

                if (!available.Contains(extension))
                {
                    if (requested[extension]) // if required
                    {
                        throw new ArgumentException($"Vulkan {extensionType} {extension} is not available!");
                    }
                    else
                    {
                        Console.WriteLine($"Optional {extensionType} is not present!");
                        continue;
                    }
                }

                result.Add(extension);
            }

            return result;
        }

        private unsafe void CreateInstance(string title, Version version)
        {
            using var marshal = new StringMarshal();

            var extensions = ChooseExtensions(null, VulkanExtensionType.Extension);
            var layers = ChooseExtensions(null, VulkanExtensionType.Layer);

            extensions.CreateNativeStringArray(extensionPtr =>
            {
                layers.CreateNativeStringArray(layerPtr =>
                {
                    var appInfo = VulkanUtilities.Init<ApplicationInfo>() with
                    {
                        PApplicationName = marshal.MarshalString(title),
                        ApplicationVersion = VulkanUtilities.MakeVersion(version),
                        PEngineName = marshal.MarshalString("N/A"),
                        EngineVersion = Vk.MakeVersion(1, 0, 0),
                        ApiVersion = Vk.Version12
                    };

                    var createInfo = VulkanUtilities.Init<InstanceCreateInfo>() with
                    {
                        PApplicationInfo = &appInfo,
                        EnabledExtensionCount = (uint)extensions.Count,
                        PpEnabledExtensionNames = extensionPtr,
                        EnabledLayerCount = (uint)layers.Count,
                        PpEnabledLayerNames = layerPtr,
                    };

                    var instance = new Instance();
                    API.CreateInstance(&createInfo, null, &instance).Assert(result =>
                    {
                        throw new ArgumentException($"Failed to create Vulkan instance: {result}");
                    });

                    mInstance = instance;
                }, marshal);
            }, marshal);
        }

        public event Action<DebugUtilsMessageSeverityFlagsEXT, DebugUtilsMessageTypeFlagsEXT, DebugUtilsMessengerCallbackDataEXT>? DebugMessage;
        private unsafe uint DebugMessengerCallback(DebugUtilsMessageSeverityFlagsEXT messageSeverity, DebugUtilsMessageTypeFlagsEXT messageTypes, DebugUtilsMessengerCallbackDataEXT* callbackData, void* userData)
        {
            DebugMessage?.Invoke(messageSeverity, messageTypes, *callbackData);
            return Vk.False;
        }

        private unsafe void CreateDebugMessenger()
        {
#if !DEBUG
            return;
#endif

            var instance = mInstance!.Value;
            var apiFunction = API.GetProcAddress<PFN_vkCreateDebugUtilsMessengerEXT>(instance);

            if (apiFunction is null)
            {
                return;
            }

            var createInfo = VulkanUtilities.Init<DebugUtilsMessengerCreateInfoEXT>() with
            {
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.InfoBitExt | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugMessengerCallback,
                PUserData = null
            };

            var debugMessenger = new DebugUtilsMessengerEXT();
            apiFunction(instance, &createInfo, null, &debugMessenger).Assert(result =>
            {
                throw new ArgumentException($"Failed to create debug messenger: {result}");
            });

            mDebugMessenger = debugMessenger;
        }

        private unsafe VulkanPhysicalDevice ChoosePhysicalDevice()
        {
            var instance = mInstance!.Value;

            uint deviceCount = 0;
            API.EnumeratePhysicalDevices(instance, &deviceCount, null);

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* ptr = devices)
            {
                API.EnumeratePhysicalDevices(instance, &deviceCount, ptr);
            }

            var scoredDevices = new List<ScoredVulkanDevice>();
            foreach (var device in devices)
            {
                var physicalDevice = new VulkanPhysicalDevice(device);
                scoredDevices.Add(new ScoredVulkanDevice
                {
                    Device = physicalDevice,
                    Score = mDeviceScorer.ScoreDevice(physicalDevice, this)
                });
            }

            scoredDevices.Sort((lhs, rhs) => -lhs.Score.CompareTo(rhs.Score));
            if (scoredDevices.Count > 0)
            {
                return scoredDevices[0].Device;
            }

            throw new ArgumentException("Failed to find a valid Vulkan device!");
        }

        private void CreateDevice()
        {
            var device = ChoosePhysicalDevice();

            // todo: additional queue families

            mDevice = new VulkanDevice(new VulkanDeviceCreateInfo
            {
                Device = device,
                Extensions = ChooseExtensions(device, VulkanExtensionType.Extension),
                Layers = ChooseExtensions(device, VulkanExtensionType.Layer),
                AdditionalQueueFamilies = Array.Empty<int>()
            });
        }

        public void Dispose()
        {
            if (!mInitialized)
            {
                return;
            }

            Dispose(true);
            mInitialized = false;
        }

        private unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                mRequestedExtensions.Clear();
            }

            mDevice?.Dispose();

            var instance = mInstance!.Value;
            if (mDebugMessenger is not null)
            {
                var apiFunction = API.GetProcAddress<PFN_vkDestroyDebugUtilsMessengerEXT>(instance);
                if (apiFunction is null)
                {
                    throw new InvalidOperationException("Failed to destroy debug messenger - function not found!");
                }
                else
                {
                    apiFunction(instance, mDebugMessenger.Value, null);
                    mDebugMessenger = null;
                }
            }

            API.DestroyInstance(instance, null);
            mInstance = null;
        }

        private Instance? mInstance;
        private DebugUtilsMessengerEXT? mDebugMessenger;
        private VulkanDevice? mDevice;
        private readonly Dictionary<VulkanExtensionLevel, Dictionary<VulkanExtensionType, Dictionary<string, bool>>> mRequestedExtensions;
        private readonly IGraphicsDeviceScorer mDeviceScorer;
        private bool mInitialized;
    }
}