﻿using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

        public static unsafe void CopyFromCPU<T>(this IDeviceBuffer buffer, T data) where T : unmanaged
        {
            buffer.CopyFromCPU(&data, sizeof(T));
        }

        public static unsafe void CopyToCPU<T>(this IDeviceBuffer buffer, out T data) where T : unmanaged
        {
            fixed (T* ptr = &data)
            {
                buffer.CopyToCPU(ptr, sizeof(T));
            }
        }

        public static ITexture LoadTexture<T>(this IGraphicsContext context, Image<T> image, DeviceImageFormat format) where T : unmanaged, IPixel<T>
        {
            var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            var texture = context.LoadTexture(image, format, commandList, out IDeviceBuffer buffer);
            commandList.End();

            queue.Submit(commandList, true);
            return texture;
        }

        public static ITexture LoadTexture<T>(this IGraphicsContext context, Image<T> image, DeviceImageFormat format, ICommandList commandList, out IDeviceBuffer buffer) where T : unmanaged, IPixel<T>
        {
            var deviceImage = context.CreateDeviceImage(new DeviceImageInfo
            {
                Size = image.Size,
                Usage = DeviceImageUsageFlags.Render | DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination,
                Format = format
            });

            int pixelCount = image.Width * image.Height;
            var pixelBuffer = new T[pixelCount];
            image.CopyPixelDataTo(pixelBuffer);

            int bufferSize = pixelCount * Marshal.SizeOf<T>();
            buffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);
            buffer.CopyFromCPU(pixelBuffer);

            var renderLayout = deviceImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            deviceImage.TransitionLayout(commandList, deviceImage.Layout, renderLayout);
            deviceImage.CopyFromBuffer(commandList, buffer, renderLayout);

            deviceImage.Layout = renderLayout;
            return deviceImage.CreateTexture(true);
        }

        public static void MapStructure<T>(this IDeviceBuffer buffer, IPipeline pipeline, string resourceName, T data) where T : unmanaged
        {
            buffer.Map(mapped =>
            {
                pipeline.MapStructure(mapped, resourceName, data);
            });
        }

        public static unsafe void MapStructure<T>(this IPipeline pipeline, Span<byte> destination, string resourceName, T data) where T : unmanaged
        {
            int count = MapFields(pipeline, destination, resourceName, string.Empty, &data, typeof(T));
            if (count == 0)
            {
                fixed (byte* spanPointer = destination)
                {
                    Buffer.MemoryCopy(&data, spanPointer, destination.Length, sizeof(T));
                }
            }
        }

        private static unsafe int MapFields(IPipeline pipeline, Span<byte> destination, string resourceName, string baseExpression, void* data, Type type)
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

                    int count = MapFields(pipeline, destination, resourceName, expression, cpuPointer, field.FieldType);
                    if (count == 0)
                    {
                        int gpuOffset = pipeline.GetBufferOffset(resourceName, expression);
                        if (gpuOffset < 0)
                        {
                            skip = true;
                            break;
                        }

                        fixed (byte* spanPointer = destination.Slice(gpuOffset))
                        {
                            Buffer.MemoryCopy(cpuPointer, spanPointer, fieldSize, fieldSize);
                        }
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
