using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics
{
    public static class GraphicsUtilities
    {
        public static unsafe void CopyFromCPU<T>(this IDeviceBuffer buffer, T[] data) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyFromCPU(ptr, data.Length * sizeof(T));
            }
        }

        public static unsafe void CopyToCPU<T>(this IDeviceBuffer buffer, T[] data) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyToCPU(ptr, data.Length * sizeof(T));
            }
        }

        public static unsafe void MapStructure<T>(this IDeviceBuffer buffer, IPipeline pipeline, string resourceName, T data) where T : unmanaged
        {
            int count = MapFields(buffer, pipeline, resourceName, string.Empty, &data, typeof(T));
            if (count == 0)
            {
                buffer.CopyFromCPU(&data, sizeof(T));
            }
        }

        private static unsafe int MapFields(this IDeviceBuffer buffer, IPipeline pipeline, string resourceName, string baseExpression, void* data, Type type)
        {
            if (type.IsPrimitive)
            {
                return 0;
            }

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var expressionBase = baseExpression.Length > 0 ? baseExpression + '.' : baseExpression;

            int result = 0;
            foreach (var field in fields)
            {
                var fixedBufferAttribute = field.GetCustomAttribute<FixedBufferAttribute>();
                var fieldType = fixedBufferAttribute?.ElementType ?? field.FieldType;
                var elementCount = fixedBufferAttribute?.Length ?? 1;

                bool skip = false;
                for (int i = 0; i < elementCount; i++)
                {
                    string expression = baseExpression + field.Name;
                    if (fixedBufferAttribute is not null)
                    {
                        expression += $"[{i}]";
                    }

                    int fieldSize;
                    if (!fieldType.IsGenericType)
                    {
                        fieldSize = Marshal.SizeOf(fieldType);
                    }
                    else
                    {
                        var method = new DynamicMethod("DynamicSizeOf", typeof(int), null);
                        var generator = method.GetILGenerator();

                        generator.Emit(OpCodes.Sizeof, fieldType);
                        generator.Emit(OpCodes.Ret);

                        fieldSize = (int)method.Invoke(null, null)!;
                    }

                    nint cpuOffset = Marshal.OffsetOf(type, field.Name) + (i * fieldSize);
                    void* cpuPointer = (void*)((nint)data + cpuOffset);

                    int count = MapFields(buffer, pipeline, resourceName, expression, cpuPointer, field.FieldType);
                    if (count == 0)
                    {
                        int gpuOffset = pipeline.GetBufferOffset(resourceName, expression);
                        if (gpuOffset < 0)
                        {
                            skip = true;
                            break;
                        }

                        buffer.CopyFromCPU(cpuPointer, fieldSize, gpuOffset);
                    }
                }

                if (!skip)
                {
                    result++;
                }
            }

            return result;
        }
    }
}
