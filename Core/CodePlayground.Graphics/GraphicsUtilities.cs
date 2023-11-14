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
                    string expression = expressionBase + field.Name;
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

        public static unsafe bool Set<T>(this IReflectionView reflectionView, Span<byte> data, string resource, string name, T value) where T : unmanaged
        {
            int offset = reflectionView.GetBufferOffset(resource, name);
            if (offset < 0)
            {
                return false;
            }

            MemoryMarshal.Write(data[offset..], ref value);
            return true;
        }

        public static bool Set<T>(this IDeviceBuffer buffer, IReflectionView reflectionView, string resource, string name, T value) where T : unmanaged
        {
            bool result = false;
            buffer.Map(data => result = Set(reflectionView, data, resource, name, value));

            return result;
        }

        public static void Set<T>(this IReflectionNode node, Span<byte> data, T value) where T : unmanaged
        {
            var slice = data[node.Offset..];
            MemoryMarshal.Write(slice, ref value);
        }

        public static void Set<T>(this IDeviceBuffer buffer, IReflectionNode node, T value) where T : unmanaged
        {
            buffer.Map(data => Set(node, data, value));
        }

        public static bool Set<T>(this IReflectionNode node, Span<byte> data, string name, T value) where T : unmanaged
        {
            var child = node.Find(name);
            if (child is null)
            {
                return false;
            }

            Set(child, data, value);
            return true;
        }

        public static bool Set<T>(this IDeviceBuffer buffer, IReflectionNode node, string name, T value) where T : unmanaged
        {
            bool result = false;
            buffer.Map(data => result = Set(node, data, name, value));

            return result;
        }

        public static GPUContextScope Context(this ICommandList commandList, GPUQueueType queueType = GPUQueueType.Graphics, int node = 0)
        {
            return new GPUContextScope(commandList.Address, queueType, node);
        }

        public static void TransitionLayout(this IDeviceImage image, ICommandList commandList, DeviceImageLayoutName source, DeviceImageLayoutName destination)
        {
            if (source == destination)
            {
                return;
            }

            var sourceLayout = image.GetLayout(source);
            var destinationLayout = image.GetLayout(destination);

            image.TransitionLayout(commandList, sourceLayout, destinationLayout);
        }

        public static void TransitionLayout(this IDeviceImage image, ICommandList commandList, DeviceImageLayoutName source, IDeviceImageLayout destination)
        {
            if (source == destination.Name)
            {
                return;
            }

            var sourceLayout = image.GetLayout(source);
            image.TransitionLayout(commandList, sourceLayout, destination);
        }

        public static void TransitionLayout(this IDeviceImage image, ICommandList commandList, IDeviceImageLayout source, DeviceImageLayoutName destination)
        {
            if (source.Name == destination)
            {
                return;
            }

            var destinationLayout = image.GetLayout(destination);
            image.TransitionLayout(commandList, source, destinationLayout);
        }

        public static void CopyToBuffer(this IDeviceImage image, ICommandList commandList, ImageSelection source, IDeviceBuffer destination, DeviceImageLayoutName currentLayout)
        {
            var layout = image.GetLayout(currentLayout);
            image.CopyToBuffer(commandList, source, destination, layout);
        }

        public static void CopyFromBuffer(this IDeviceImage image, ICommandList commandList, IDeviceBuffer source, ImageSelection destination, DeviceImageLayoutName currentLayout)
        {
            var layout = image.GetLayout(currentLayout);
            image.CopyFromBuffer(commandList, source, destination, layout);
        }

        public static void CopyToBuffer(this IDeviceImage image, ICommandList commandList, IDeviceBuffer destination, DeviceImageLayoutName currentLayout)
        {
            var layout = image.GetLayout(currentLayout);
            image.CopyToBuffer(commandList, destination, layout);
        }

        public static void CopyFromBuffer(this IDeviceImage image, ICommandList commandList, IDeviceBuffer source, DeviceImageLayoutName currentLayout)
        {
            var layout = image.GetLayout(currentLayout);
            image.CopyFromBuffer(commandList, source, layout);
        }
    }
}
