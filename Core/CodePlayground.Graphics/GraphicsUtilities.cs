using Optick.NET;
using SixLabors.ImageSharp;
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
        public static unsafe void CopyFromCPU<T>(this IDeviceBuffer buffer, T[] data, int offset = 0) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyFromCPU(ptr, data.Length * sizeof(T), offset);
            }
        }

        public static unsafe void CopyFromCPU<T>(this IDeviceBuffer buffer, ReadOnlySpan<T> data, int offset = 0) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyFromCPU(ptr, data.Length * sizeof(T), offset);
            }
        }

        public static unsafe void CopyFromCPU<T>(this IDeviceBuffer buffer, T data, int offset = 0) where T : unmanaged
        {
            buffer.CopyFromCPU(&data, sizeof(T), offset);
        }

        public static unsafe void CopyToCPU<T>(this IDeviceBuffer buffer, T[] data, int offset = 0) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyToCPU(ptr, data.Length * sizeof(T), offset);
            }
        }

        public static unsafe void CopyToCPU<T>(this IDeviceBuffer buffer, ReadOnlySpan<T> data, int offset = 0) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyToCPU(ptr, data.Length * sizeof(T), offset);
            }
        }

        public static unsafe void CopyToCPU<T>(this IDeviceBuffer buffer, out T data, int offset = 0) where T : unmanaged
        {
            fixed (T* ptr = &data)
            {
                buffer.CopyToCPU(ptr, sizeof(T), offset);
            }
        }

        public static ITexture LoadTexture<T>(this IGraphicsContext context, Image<T> image, DeviceImageFormat format) where T : unmanaged, IPixel<T>
        {
            var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();
            commandList.Begin();

            ITexture texture;
            using (commandList.Context(GPUQueueType.Transfer))
            {
                texture = context.LoadTexture(image, format, commandList);
            }

            commandList.End();
            queue.Submit(commandList, true);
            return texture;
        }

        public static ITexture LoadTexture<T>(this IGraphicsContext context, Image<T> image, DeviceImageFormat format, ICommandList commandList) where T : unmanaged, IPixel<T>
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
            var buffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);
            buffer.CopyFromCPU(pixelBuffer);

            var renderLayout = deviceImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            deviceImage.TransitionLayout(commandList, deviceImage.Layout, renderLayout);
            deviceImage.CopyFromBuffer(commandList, buffer, renderLayout);
            commandList.PushStagingObject(buffer);

            deviceImage.Layout = renderLayout;
            return deviceImage.CreateTexture(true);
        }

        public static void MapStructure<T>(this IDeviceBuffer buffer, IReflectionView reflectionView, string resourceName, T data) where T : unmanaged
        {
            buffer.Map(mapped =>
            {
                reflectionView.MapStructure(mapped, resourceName, data);
            });
        }

        public static unsafe void MapStructure<T>(this IReflectionView reflectionView, Span<byte> destination, string resourceName, T data) where T : unmanaged
        {
            int count = MapFields(reflectionView, destination, resourceName, string.Empty, &data, typeof(T));
            if (count == 0)
            {
                fixed (byte* spanPointer = destination)
                {
                    Buffer.MemoryCopy(&data, spanPointer, destination.Length, sizeof(T));
                }
            }
        }

        private static unsafe int MapFields(IReflectionView reflectionView, Span<byte> destination, string resourceName, string baseExpression, void* data, Type type)
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

                    int count = MapFields(reflectionView, destination, resourceName, expression, cpuPointer, field.FieldType);
                    if (count == 0)
                    {
                        int gpuOffset = reflectionView.GetBufferOffset(resourceName, expression);
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

        public static GPUContextScope Context(this ICommandList commandList, GPUQueueType queueType = GPUQueueType.Graphics, int node = 0)
        {
            return new GPUContextScope(commandList.Address, queueType, node);
        }

        public static void TransitionLayout(this IDeviceImage image, ICommandList commandList, DeviceImageLayoutName source, DeviceImageLayoutName destination)
        {
            var sourceLayout = image.GetLayout(source);
            var destinationLayout = image.GetLayout(destination);

            image.TransitionLayout(commandList, sourceLayout, destinationLayout);
        }

        public static void TransitionLayout(this IDeviceImage image, ICommandList commandList, DeviceImageLayoutName source, object destination)
        {
            var sourceLayout = image.GetLayout(source);
            image.TransitionLayout(commandList, sourceLayout, destination);
        }

        public static void TransitionLayout(this IDeviceImage image, ICommandList commandList, object source, DeviceImageLayoutName destination)
        {
            var destinationLayout = image.GetLayout(destination);
            image.TransitionLayout(commandList, source, destinationLayout);
        }
    }
}
