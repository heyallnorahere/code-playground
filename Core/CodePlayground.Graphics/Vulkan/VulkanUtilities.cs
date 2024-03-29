using Silk.NET.Core;
using Silk.NET.Core.Attributes;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

using VulkanResult = Silk.NET.Vulkan.Result;
using SpvResult = Silk.NET.SPIRV.Cross.Result;

namespace CodePlayground.Graphics.Vulkan
{
    public static class VulkanUtilities
    {
        public static CommandQueueFlags ConvertQueueFlags(QueueFlags flags)
        {
            var flagValues = flags.SplitFlags();
            CommandQueueFlags result = 0;

            foreach (var flagValue in flagValues)
            {
                result |= flagValue switch
                {
                    QueueFlags.GraphicsBit => CommandQueueFlags.Graphics,
                    QueueFlags.ComputeBit => CommandQueueFlags.Compute,
                    QueueFlags.TransferBit => CommandQueueFlags.Transfer,
                    _ => 0
                };
            }

            return result;
        }

        private unsafe static T Zeroed<T>() where T : unmanaged
        {
            int size = sizeof(T);
            var buffer = new byte[size];
            Array.Fill(buffer, (byte)0);

            fixed (byte* ptr = buffer)
            {
                return Marshal.PtrToStructure<T>((nint)ptr);
            }
        }

        public static T Init<T>() where T : unmanaged
        {
            object obj = Zeroed<T>();

            var type = typeof(T);
            var field = type.GetField("SType", BindingFlags.Public | BindingFlags.Instance);

            if (field is not null)
            {
                if (!Enum.TryParse(type.Name, true, out StructureType value))
                {
                    throw new ArgumentException("Could not find a matching structure type!");
                }

                field.SetValue(obj, value);
            }


            return (T)obj;
        }

        public static T Init<T>(StructureType structureType) where T : unmanaged
        {
            object result = Zeroed<T>();

            var type = typeof(T);
            var field = type.GetField("SType", BindingFlags.Public | BindingFlags.Instance);

            if (field is null)
            {
                throw new ArgumentException("No SType field found!");
            }

            field.SetValue(result, structureType);
            return (T)result;
        }

        public static Version32 MakeVersion(Version version)
        {
            uint major = (uint)Math.Max(version.Major, 0);
            uint minor = (uint)Math.Max(version.Minor, 0);
            uint patch = (uint)Math.Max(version.Build, 0);

            return Vk.MakeVersion(major, minor, patch);
        }

        private static void DumpCheckpointData()
        {
            var app = Application.Instance;
            if (app is not GraphicsApplication graphicsApplication)
            {
                return;
            }

            var context = graphicsApplication.GraphicsContext;
            if (context is not VulkanContext vulkanContext)
            {
                return;
            }

            var device = vulkanContext.Device;
            var api = VulkanContext.API;

            if (!api.TryGetDeviceExtension(device.PhysicalDevice.Instance, device.Device, out NVDeviceDiagnosticCheckpoints extension))
            {
                return;
            }

            Console.WriteLine($"Dumping checkpoint data collected");
            foreach (int family in device.QueueFamilies)
            {
                var queue = device.GetQueue(family);
                Console.WriteLine($"\tQueue family {family}: {queue.Usage}");

                unsafe
                {
                    uint entryCount = 0;
                    extension.GetQueueCheckpointData(queue.Queue, &entryCount, null);

                    if (entryCount > 0)
                    {
                        var entries = new CheckpointDataNV[entryCount];
                        Array.Fill(entries, Init<CheckpointDataNV>());

                        fixed (CheckpointDataNV* entriesPtr = entries)
                        {
                            extension.GetQueueCheckpointData(queue.Queue, &entryCount, entriesPtr);
                        }

                        foreach (var entry in entries)
                        {
                            var identifier = Marshal.PtrToStringUTF8((nint)entry.PCheckpointMarker);
                            Console.WriteLine($"\t\t{identifier}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"No entries found");
                    }
                }
            }
        }

        public static void Assert(this VulkanResult result, Action<VulkanResult>? onFail = null)
        {
            if (result != VulkanResult.Success)
            {
                if (onFail is null)
                {
                    if (result == Result.ErrorDeviceLost)
                    {
                        DumpCheckpointData();
                    }

                    throw new Exception($"Vulkan error caught: {result}");
                }
                else
                {
                    onFail.Invoke(result);
                }
            }
        }

        public static void Assert(this SpvResult result, Action<SpvResult>? onFail = null)
        {
            if (result != SpvResult.Success)
            {
                if (onFail is null)
                {
                    throw new Exception($"SPIRV Cross error caught: {result}");
                }
                else
                {
                    onFail.Invoke(result);
                }
            }
        }

        internal unsafe delegate void StringArrayCallback(byte** result);
        internal unsafe static void CreateNativeStringArray(this IEnumerable<string> data, StringArrayCallback callback, StringMarshal? marshal = null)
        {
            using var createStringArrayEvent = Profiler.Event();
            var usedMarshal = marshal ?? new StringMarshal();

            var list = new List<string>(data);
            var result = new byte*[list.Count];

            for (int i = 0; i < list.Count; i++)
            {
                result[i] = usedMarshal.MarshalString(list[i]);
            }

            fixed (byte** ptr = result)
            {
                callback(ptr);
            }

            if (marshal is null)
            {
                usedMarshal.Dispose();
            }
        }

        private static string GetExtensionName<T>()
        {
            using var getNameEvent = Profiler.Event();

            var extensionType = typeof(T);
            var extensionAttribute = extensionType.GetCustomAttribute<ExtensionAttribute>();

            if (extensionAttribute is null)
            {
                throw new ArgumentException("The passed type is not an extension!");
            }

            return extensionAttribute.Name;
        }

        public static T GetInstanceExtension<T>(this Vk api, Instance instance) where T : NativeExtension<Vk>
        {
            using var getExtensionEvent = Profiler.Event();

            string extensionName = GetExtensionName<T>();
            if (!api.TryGetInstanceExtension(instance, out T extension))
            {
                throw new ArgumentException($"Instance extension {extensionName} not loaded!");
            }

            return extension;
        }

        public static T GetDeviceExtension<T>(this Vk api, Instance instance, Device device) where T : NativeExtension<Vk>
        {
            using var getExtensionEvent = Profiler.Event();

#if __IOS__
            var extension = (T)Activator.CreateInstance(typeof(T), new LamdaNativeContext(name => api.GetDeviceProcAddr(device, name)))!;
#else
            string extensionName = GetExtensionName<T>();
            if (!api.TryGetDeviceExtension(instance, device, out T extension))
            {
                throw new ArgumentException($"Device extension {extensionName} not loaded!");
            }
#endif

            return extension;
        }

        private unsafe static T? GetProcAddress<T>(Func<string, PfnVoidFunction> api) where T : Delegate
        {
            using var getAddressEvent = Profiler.Event();
            const string prefix = "PFN_";

            var name = typeof(T).Name;
            if (!name.StartsWith(prefix))
            {
                throw new ArgumentException("Invalid delegate name!");
            }

            string functionName = name[prefix.Length..];
            var voidFunction = api(functionName);

            try
            {
                return Marshal.GetDelegateForFunctionPointer<T>((nint)voidFunction.Handle);
            }
            catch (ArgumentNullException)
            {
                return null;
            }
        }

        public static T? GetProcAddress<T>(this Vk api, Instance instance) where T : Delegate
        {
            return GetProcAddress<T>(name => api.GetInstanceProcAddr(instance, name));
        }

        public static T? GetProcAddress<T>(this Vk api, Device device) where T : Delegate
        {
            return GetProcAddress<T>(name => api.GetDeviceProcAddr(device, name));
        }

        public static bool HasStencil(this Format format)
        {
            using var hasStencilEvent = Profiler.Event();

            var formatName = format.ToString();
            return formatName.EndsWith(nameof(Format.S8Uint));
        }

        public static SharingMode FindSharingMode(this VulkanPhysicalDevice physicalDevice, out uint[]? familyIndices, out uint indexCount)
        {
            using var findFamiliesEvent = Profiler.Event();

            familyIndices = null;
            indexCount = 0;

            var uniqueIndices = physicalDevice.FindQueueTypes().Values.ToHashSet();
            if (uniqueIndices.Count != 1)
            {
                familyIndices = uniqueIndices.Select(Convert.ToUInt32).ToArray();
                indexCount = (uint)uniqueIndices.Count;

                return SharingMode.Concurrent;
            }
            else
            {
                return SharingMode.Exclusive;
            }
        }
    }
}