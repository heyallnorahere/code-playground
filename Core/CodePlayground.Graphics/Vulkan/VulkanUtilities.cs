using Optick.NET;
using Silk.NET.Core;
using Silk.NET.Core.Attributes;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Spirzza.Interop.SpirvCross;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

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

        public static void Assert(this Result result, Action<Result>? onFail = null)
        {
            if (result != Result.Success)
            {
                if (onFail is null)
                {
                    throw new Exception($"Vulkan error caught: {result}");
                }
                else
                {
                    onFail.Invoke(result);
                }
            }
        }

        public static void Assert(this spvc_result result, Action<spvc_result>? onFail = null)
        {
            if (result != spvc_result.SPVC_SUCCESS)
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
            using var createStringArrayEvent = OptickMacros.Event();
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
            using var getNameEvent = OptickMacros.Event();

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
            using var getExtensionEvent = OptickMacros.Event();

            string extensionName = GetExtensionName<T>();
            if (!api.TryGetInstanceExtension(instance, out T extension))
            {
                throw new ArgumentException($"Instance extension {extensionName} not loaded!");
            }

            return extension;
        }

        public static T GetDeviceExtension<T>(this Vk api, Instance instance, Device device) where T : NativeExtension<Vk>
        {
            using var getExtensionEvent = OptickMacros.Event();

            string extensionName = GetExtensionName<T>();
            if (!api.TryGetDeviceExtension(instance, device, out T extension))
            {
                throw new ArgumentException($"Device extension {extensionName} not loaded!");
            }

            return extension;
        }

        private unsafe static T? GetProcAddress<T>(Func<string, PfnVoidFunction> api) where T : Delegate
        {
            using var getAddressEvent = OptickMacros.Event();
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
            using var hasStencilEvent = OptickMacros.Event();

            var formatName = format.ToString();
            return formatName.EndsWith(nameof(Format.S8Uint));
        }

        public static SharingMode FindSharingMode(this VulkanPhysicalDevice physicalDevice, out uint[]? familyIndices, out uint indexCount)
        {
            using var findFamiliesEvent = OptickMacros.Event();

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