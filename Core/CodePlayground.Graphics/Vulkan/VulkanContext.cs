using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using VMASharp;

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

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class VulkanAPIVersionAttribute : Attribute
    {
        public VulkanAPIVersionAttribute(string version)
        {
            Version = Version.Parse(version);
        }

        public Version Version { get; }
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

                score += totalPresent * 500;
            }

            var queueTypes = Enum.GetValues<CommandQueueFlags>();
            var availableQueues = physicalDevice.FindQueueTypes();
            score += availableQueues.Count * 1000 / queueTypes.Length;

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

    public sealed class VulkanSemaphore : IDisposable
    {
        public unsafe VulkanSemaphore(VulkanDevice device)
        {
            mDevice = device;
            mDisposed = false;

            var createInfo = VulkanUtilities.Init<SemaphoreCreateInfo>();
            var api = VulkanContext.API;
            api.CreateSemaphore(mDevice.Device, createInfo, null, out mSemaphore).Assert();
        }

        ~VulkanSemaphore()
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
            var api = VulkanContext.API;
            api.DestroySemaphore(mDevice.Device, mSemaphore, null);
        }

        public Semaphore Semaphore => mSemaphore;

        private readonly Semaphore mSemaphore;
        private readonly VulkanDevice mDevice;
        private bool mDisposed;
    }

    public sealed class VulkanContext : IGraphicsContext
    {
        public static Vk API { get; }
        static VulkanContext()
        {
            API = Vk.GetApi();
        }

        public VulkanContext()
        {
            mInstance = null;
            mDebugMessenger = null;
            mDevice = null;

            mInitialized = false;
            mRequestedExtensions = new Dictionary<VulkanExtensionLevel, Dictionary<VulkanExtensionType, Dictionary<string, bool>>>();
            DeviceScorer = new DefaultVulkanDeviceScorer();
        }

        ~VulkanContext()
        {
            if (mInitialized)
            {
                Dispose(false);
                mInitialized = false;
            }
        }

        public bool IsApplicable(WindowOptions options)
        {
            return options.API.Equals(GraphicsAPI.DefaultVulkan);
        }

        public unsafe void Initialize(IWindow? window, GraphicsApplication application)
        {
            if (mInitialized)
            {
                throw new InvalidOperationException("Context has already been initialized!");
            }

            var title = application.Title;
            var version = application.Version;

            var apiVersionAttribute = application.GetType().GetCustomAttribute<VulkanAPIVersionAttribute>();
            mVulkanVersion = apiVersionAttribute?.Version ?? new Version(1, 2, 0);
            Console.WriteLine($"Initializing Vulkan for application {title} v{version} (API version {mVulkanVersion})");

            RetrieveRequestedExtensionsAndLayers(application);
            CreateInstance(title, version, mVulkanVersion);
            CreateDebugMessenger();

            var additionalQueueFamilies = new List<int>();
            SurfaceKHR? windowSurface = null;

            var physicalDevice = ChoosePhysicalDevice();
            if (window is not null)
            {
                windowSurface = CreateWindowSurface(physicalDevice, window, out int presentQueue);
                additionalQueueFamilies.Add(presentQueue);
            }

            mDevice = new VulkanDevice(new VulkanDeviceCreateInfo
            {
                Device = physicalDevice,
                Extensions = ChooseExtensions(physicalDevice, VulkanExtensionType.Extension),
                Layers = ChooseExtensions(physicalDevice, VulkanExtensionType.Layer),
                AdditionalQueueFamilies = additionalQueueFamilies
            });

            mAllocator = new VulkanMemoryAllocator(new VulkanMemoryAllocatorCreateInfo
            {
                VulkanAPIVersion = VulkanUtilities.MakeVersion(mVulkanVersion),
                VulkanAPIObject = API,
                Instance = mInstance!.Value,
                PhysicalDevice = physicalDevice.Device,
                LogicalDevice = mDevice.Device,
                PreferredLargeHeapBlockSize = 0,
                HeapSizeLimits = null,
                FrameInUseCount = 0
            });

            if (windowSurface is not null)
            {
                mSwapchain = new VulkanSwapchain(windowSurface.Value, this, mInstance!.Value, window!);
            }

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
                if (extension.Level == VulkanExtensionLevel.Device && extension.Type == VulkanExtensionType.Layer)
                {
                    Console.WriteLine($"Warning for layer {extension.Name}: device layers are deprecated");
                }

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
                            API.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, ptr);
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
                            API.EnumerateDeviceExtensionProperties(device.Device, (byte*)null, &extensionCount, ptr);
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
                            API.EnumerateInstanceLayerProperties(&layerCount, ptr);
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
                            API.EnumerateDeviceLayerProperties(device.Device, &layerCount, ptr);
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

        private unsafe void CreateInstance(string title, Version appVersion, Version apiVersion)
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
                        ApplicationVersion = VulkanUtilities.MakeVersion(appVersion),
                        PEngineName = marshal.MarshalString("N/A"),
                        EngineVersion = Vk.MakeVersion(1, 0, 0),
                        ApiVersion = VulkanUtilities.MakeVersion(apiVersion)
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
                if (messageSeverity.HasFlag(severityFlag))
                {
                    if (severityName.Length > 0)
                    {
                        severityName += ", ";
                    }

                    severityName += severityNames[severityFlag];
                }
            }

            var vulkanMessage = Marshal.PtrToStringAnsi((nint)callbackData->PMessage);
            var message = $"Vulkan validation layer: [{severityName}] {vulkanMessage}";

            if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt))
            {
                throw new InvalidOperationException(message);
            }
            else
            {
                Console.WriteLine(message);
            }

            return Vk.False;
        }

        [Conditional("DEBUG")]
        private unsafe void CreateDebugMessenger()
        {
            var instance = mInstance!.Value;
            if (!API.TryGetInstanceExtension(instance, out ExtDebugUtils debugUtils))
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
            debugUtils.CreateDebugUtilsMessenger(instance, &createInfo, null, &debugMessenger).Assert();

            mDebugMessenger = debugMessenger;
        }

        private unsafe VulkanPhysicalDevice ChoosePhysicalDevice()
        {
            var instance = mInstance!.Value;

            uint deviceCount = 0;
            API.EnumeratePhysicalDevices(instance, &deviceCount, null).Assert();

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* ptr = devices)
            {
                API.EnumeratePhysicalDevices(instance, &deviceCount, ptr);
            }

            var scoredDevices = new List<ScoredVulkanDevice>();
            foreach (var device in devices)
            {
                var physicalDevice = new VulkanPhysicalDevice(device, instance);
                scoredDevices.Add(new ScoredVulkanDevice
                {
                    Device = physicalDevice,
                    Score = DeviceScorer.ScoreDevice(physicalDevice, this)
                });
            }

            scoredDevices.Sort((lhs, rhs) => -lhs.Score.CompareTo(rhs.Score));
            if (scoredDevices.Count > 0)
            {
                var device = scoredDevices[0].Device;
                Console.WriteLine($"Chose device: {device.Name}");

                return device;
            }

            throw new ArgumentException("Failed to find a valid Vulkan device!");
        }

        private unsafe SurfaceKHR CreateWindowSurface(VulkanPhysicalDevice physicalDevice, IWindow window, out int presentQueue)
        {
            var instance = mInstance!.Value;
            SurfaceKHR surface = window.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();

            var extension = API.GetInstanceExtension<KhrSurface>(instance);
            presentQueue = VulkanSwapchain.FindPresentQueueFamily(extension, physicalDevice, surface);

            return surface;
        }

        public IShaderCompiler CreateCompiler()
        {
            return new VulkanShaderCompiler(mVulkanVersion!);
        }

        IRenderer IGraphicsContext.CreateRenderer()
        {
            return new VulkanRenderer();
        }

        IDeviceBuffer IGraphicsContext.CreateDeviceBuffer(DeviceBufferUsage usage, int size)
        {
            return new VulkanBuffer(mDevice!, mAllocator!, usage, size);
        }

        IDeviceImage IGraphicsContext.CreateDeviceImage(DeviceImageInfo info)
        {
            return new VulkanImage(mDevice!, mAllocator!, new VulkanImageCreateInfo
            {
                Size = info.Size,
                Usage = info.Usage,
                ImageType = info.ImageType,
                MipLevels = info.MipLevels,
                Format = info.Format
            });
        }

        IShader IGraphicsContext.LoadShader(IReadOnlyList<byte> data, ShaderStage stage, string entrypoint)
        {
            return new VulkanShader(mDevice!, data, stage, entrypoint);
        }

        IReflectionView IGraphicsContext.CreateReflectionView(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            return new VulkanReflectionView(shaders);
        }

        IPipeline IGraphicsContext.CreatePipeline(PipelineDescription description)
        {
            return new VulkanPipeline(this, description);
        }

        IDisposable IGraphicsContext.CreateSemaphore() => new VulkanSemaphore(mDevice!);
        IFence IGraphicsContext.CreateFence(bool signaled) => new VulkanFence(mDevice!, signaled);

        IFramebuffer IGraphicsContext.CreateFramebuffer(FramebufferInfo info, out IRenderTarget renderTarget)
        {
            var colorAttachments = new List<VulkanRenderPassAttachment>();
            VulkanRenderPassAttachment? depthAttachment = null;

            for (int i = 0; i < info.Attachments.Count; i++)
            {
                var attachment = info.Attachments[i];
                if (attachment.Image is not VulkanImage image)
                {
                    throw new ArgumentException("Attachments must be Vulkan images!");
                }

                var layout = (VulkanImageLayout?)attachment.Layout ?? image.Layout;
                var attachmentInfo = new VulkanRenderPassAttachment
                {
                    ImageFormat = image.VulkanFormat,
                    Samples = SampleCountFlags.Count1Bit, // todo: read from image
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare, // stencil is not supported yet
                    StencilStoreOp = AttachmentStoreOp.DontCare, // ^
                    InitialLayout = ((VulkanImageLayout?)attachment.InitialLayout)?.Layout ?? ImageLayout.Undefined,
                    FinalLayout = ((VulkanImageLayout?)attachment.FinalLayout ?? layout).Layout,
                    Layout = layout.Layout
                };

                switch (attachment.Type)
                {
                    case AttachmentType.Color:
                        colorAttachments.Add(attachmentInfo);
                        break;
                    case AttachmentType.DepthStencil:
                        if (i < info.Attachments.Count - 1)
                        {
                            // im just lazy lmao
                            // though it makes it easier
                            throw new ArgumentException("Depth stencil attachment must be the last element in the attachment list!");
                        }

                        depthAttachment = attachmentInfo;
                        break;
                    default:
                        throw new ArgumentException("Invalid attachment type!");
                }
            }

            var renderPass = new VulkanRenderPass(mDevice!, new VulkanRenderPassInfo
            {
                ColorAttachments = colorAttachments,
                DepthAttachment = depthAttachment,
                SubpassDependency = new VulkanSubpassDependency
                {
                    SourceStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                    SourceAccessMask = 0,
                    DestinationStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                    DestinationAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
                }
            });

            renderTarget = renderPass;
            return CreateFramebuffer(info, renderPass);
        }

        IFramebuffer IGraphicsContext.CreateFramebuffer(FramebufferInfo info, IRenderTarget renderTarget)
        {
            if (renderTarget is not VulkanRenderPass)
            {
                throw new ArgumentException("Must pass a Vulkan render pass!");
            }

            return CreateFramebuffer(info, (VulkanRenderPass)renderTarget);
        }

        private IFramebuffer CreateFramebuffer(FramebufferInfo info, VulkanRenderPass renderPass)
        {
            var views = new List<ImageView>();
            foreach (var attachment in info.Attachments)
            {
                if (attachment.Image is not VulkanImage image)
                {
                    throw new ArgumentException("Attachments must be Vulkan images!");
                }

                views.Add(image.View);
            }

            return new VulkanFramebuffer(mDevice!, new VulkanFramebufferInfo
            {
                Width = info.Width,
                Height = info.Height,
                RenderPass = renderPass,
                Attachments = views
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
            var app = (GraphicsApplication)Application.Instance;
            app.OnContextDestroyed();

            if (disposing)
            {
                mRequestedExtensions.Clear();
            }

            if (mDevice is not null)
            {
                API.DeviceWaitIdle(mDevice.Device);
            }

            mSwapchain?.Dispose();
            mSwapchain = null;

            mAllocator?.Dispose();
            mAllocator = null;

            mDevice?.Dispose();
            mDevice = null;

            var instance = mInstance!.Value;
            if (mDebugMessenger is not null)
            {
                var debugUtils = API.GetInstanceExtension<ExtDebugUtils>(instance);
                debugUtils.DestroyDebugUtilsMessenger(instance, mDebugMessenger.Value, null);

                mDebugMessenger = null;
            }

            API.DestroyInstance(instance, null);
            mInstance = null;
        }

        public IGraphicsDeviceScorer DeviceScorer { get; set; }
        public VulkanDevice Device => mDevice!;
        public VulkanSwapchain? Swapchain => mSwapchain;
        public VulkanMemoryAllocator Allocator => mAllocator!;

        public bool FlipUVs => true;
        public bool LeftHanded => true;
        public MinimumDepth MinDepth => MinimumDepth.Zero;
        public bool ViewportFlipped => true;
        public MatrixType MatrixType => MatrixType.OpenGL;

        IGraphicsDevice IGraphicsContext.Device => mDevice!;
        ISwapchain? IGraphicsContext.Swapchain => mSwapchain;

        private Version? mVulkanVersion;
        private Instance? mInstance;
        private DebugUtilsMessengerEXT? mDebugMessenger;
        private VulkanDevice? mDevice;
        private VulkanSwapchain? mSwapchain;
        private VulkanMemoryAllocator? mAllocator;

        private readonly Dictionary<VulkanExtensionLevel, Dictionary<VulkanExtensionType, Dictionary<string, bool>>> mRequestedExtensions;
        private bool mInitialized;
    }
}