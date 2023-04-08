using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace CodePlayground.Graphics.Vulkan
{
    internal static class VulkanUtilities
    {
        public static T Init<T>() where T : struct
        {
            object result = new T();

            var type = typeof(T);
            var field = type.GetField("SType", BindingFlags.Public | BindingFlags.Instance);

            if (field is not null)
            {
                if (!Enum.TryParse<StructureType>(type.Name, true, out StructureType value))
                {
                    throw new ArgumentException("Could not find a matching structure type!");
                }

                field.SetValue(result, value);
            }

            return (T)result;
        }

        public static T Init<T>(StructureType structureType) where T : struct
        {
            object result = new T();

            var type = typeof(T);
            var field = type.GetField("SType", BindingFlags.Public | BindingFlags.Instance);

            if (field is null)
            {
                throw new ArgumentException("No SType field found!");
            }

            field.SetValue(result, structureType);
            return (T)result;
        }

        public static uint MakeVersion(Version version)
        {
            uint major = (uint)version.Major;
            uint minor = (uint)version.Minor;
            uint patch = (uint)version.Revision;

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
                    onFail(result);
                }
            }
        }

        public unsafe delegate void StringArrayCallback(byte** result);
        public unsafe static void CreateNativeStringArray(this IEnumerable<string> data, StringArrayCallback callback, StringMarshal? marshal = null)
        {
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
    }
}