using Optick.NET;
using Silk.NET.Vulkan;
using System;
using VMASharp;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanBuffer : IDeviceBuffer, IBindableVulkanResource
    {
        public static BufferUsageFlags GetUsageFlags(DeviceBufferUsage usage)
        {
            return usage switch
            {
                DeviceBufferUsage.Vertex => BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                DeviceBufferUsage.Index => BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
                DeviceBufferUsage.Uniform => BufferUsageFlags.UniformBufferBit,
                DeviceBufferUsage.Storage => BufferUsageFlags.StorageBufferBit,
                DeviceBufferUsage.Staging => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                _ => throw new ArgumentException("Invalid buffer usage!")
            };
        }

        public unsafe VulkanBuffer(VulkanDevice device, VulkanMemoryAllocator allocator, DeviceBufferUsage usage, int size)
        {
            using var constructorEvent = OptickMacros.Event();

            mDevice = device;
            mAllocator = allocator;
            mID = VulkanPipeline.GenerateID();

            mUsage = usage;
            mSize = size;
            mDisposed = false;

            var createInfo = VulkanUtilities.Init<BufferCreateInfo>() with
            {
                Size = (ulong)size,
                Usage = GetUsageFlags(usage)
            };

            var physicalDevice = device.PhysicalDevice;
            var sharingMode = physicalDevice.FindSharingMode(out uint[]? indices, out uint indexCount);

            var api = VulkanContext.API;
            using (OptickMacros.Event("Device buffer creation"))
            {
                fixed (uint* indexPtr = indices)
                {
                    createInfo.SharingMode = sharingMode;
                    createInfo.QueueFamilyIndexCount = indexCount;
                    createInfo.PQueueFamilyIndices = indexPtr;

                    fixed (Silk.NET.Vulkan.Buffer* buffer = &mBuffer)
                    {
                        api.CreateBuffer(device.Device, &createInfo, null, buffer).Assert();
                    }
                }
            }

            using (OptickMacros.Event("Memory allocation"))
            {
                mAllocation = allocator.AllocateMemoryForBuffer(mBuffer, new AllocationCreateInfo
                {
                    Usage = usage switch
                    {
                        DeviceBufferUsage.Vertex => MemoryUsage.GPU_Only,
                        DeviceBufferUsage.Index => MemoryUsage.GPU_Only,
                        DeviceBufferUsage.Uniform => MemoryUsage.CPU_To_GPU,
                        DeviceBufferUsage.Storage => MemoryUsage.GPU_To_CPU,
                        DeviceBufferUsage.Staging => MemoryUsage.CPU_Only,
                        _ => throw new ArgumentException("Invalid buffer usage!")
                    }
                });

                mAllocation.BindBufferMemory(mBuffer).Assert();
            }
        }

        ~VulkanBuffer()
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
            api.DestroyBuffer(mDevice.Device, mBuffer, null);

            mAllocator.FreeMemory(mAllocation);
        }

        public unsafe void CopyFromCPU(void* address, int size, int offset = 0)
        {
            var deviceAddress = (void*)(mAllocation.Map() + (nint)offset);
            System.Buffer.MemoryCopy(address, deviceAddress, mSize, size);
            mAllocation.Unmap();
        }

        public unsafe void CopyToCPU(void* address, int size, int offset = 0)
        {
            var deviceAddress = (void*)(mAllocation.Map() + (nint)offset);
            System.Buffer.MemoryCopy(deviceAddress, address, size, mSize);
            mAllocation.Unmap();
        }

        public unsafe void Map(BufferMapCallback callback)
        {
            nint mapped = mAllocation.Map();

            var span = new Span<byte>((void*)mapped, mSize);
            callback.Invoke(span);

            mAllocation.Unmap();
        }

        void IDeviceBuffer.CopyBuffers(ICommandList commandList, IDeviceBuffer destination, int size, int srcOffset, int dstOffset)
        {
            if (commandList is not VulkanCommandBuffer || destination is not VulkanBuffer)
            {
                throw new ArgumentException("Must pass Vulkan objects!");
            }

            CopyBuffers((VulkanCommandBuffer)commandList, (VulkanBuffer)destination, size, srcOffset, dstOffset);
        }

        public void CopyBuffers(VulkanCommandBuffer commandBuffer, VulkanBuffer destination, int size, int srcOffset = 0, int dstOffset = 0)
        {
            using var copyEvent = OptickMacros.Event();
            using var gpuEvent = OptickMacros.GPUEvent("Buffer-to-buffer copy");

            var region = VulkanUtilities.Init<BufferCopy>() with
            {
                SrcOffset = (ulong)srcOffset,
                DstOffset = (ulong)dstOffset,
                Size = (ulong)size
            };

            var api = VulkanContext.API;
            api.CmdCopyBuffer(commandBuffer.Buffer, mBuffer, destination.mBuffer, 1, region);
        }

        void IDeviceBuffer.BindVertices(ICommandList commandList, int index)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer!");
            }

            BindVertices((VulkanCommandBuffer)commandList, index);
        }

        public void BindVertices(VulkanCommandBuffer commandBuffer, int index)
        {
            using var bindEvent = OptickMacros.Event();
            using var gpuBindEvent = OptickMacros.GPUEvent("Bind vertex buffer");

            var api = VulkanContext.API;
            api.CmdBindVertexBuffers(commandBuffer.Buffer, (uint)index, 1, mBuffer, 0);
        }

        void IDeviceBuffer.BindIndices(ICommandList commandList, DeviceBufferIndexType indexType)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer!");
            }

            BindIndices((VulkanCommandBuffer)commandList, indexType);
        }

        public void BindIndices(VulkanCommandBuffer commandBuffer, DeviceBufferIndexType indexType)
        {
            using var bindEvent = OptickMacros.Event();
            using var gpuBindEvent = OptickMacros.GPUEvent("Bind index buffer");

            var api = VulkanContext.API;
            api.CmdBindIndexBuffer(commandBuffer.Buffer, mBuffer, 0, indexType switch
            {
                DeviceBufferIndexType.UInt16 => IndexType.Uint16,
                DeviceBufferIndexType.UInt32 => IndexType.Uint32,
                _ => throw new ArgumentException("Invalid index type!")
            });
        }

        unsafe void IBindableVulkanResource.Bind(DescriptorSet[] sets, int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
            using var bindEvent = OptickMacros.Event();

            var bufferInfo = VulkanUtilities.Init<DescriptorBufferInfo>() with
            {
                Buffer = mBuffer,
                Offset = 0,
                Range = (ulong)mSize
            };

            var writes = new WriteDescriptorSet[sets.Length];
            for (int i = 0; i < writes.Length; i++)
            {
                writes[i] = VulkanUtilities.Init<WriteDescriptorSet>() with
                {
                    DstSet = sets[i],
                    DstBinding = (uint)binding,
                    DstArrayElement = (uint)index,
                    DescriptorCount = 1,
                    DescriptorType = mUsage switch
                    {
                        DeviceBufferUsage.Uniform => DescriptorType.UniformBuffer,
                        DeviceBufferUsage.Storage => DescriptorType.StorageBuffer,
                        _ => throw new InvalidOperationException($"Cannot bind a buffer of type {mUsage}")
                    },
                    PBufferInfo = &bufferInfo
                };
            }

            var api = VulkanContext.API;
            api.UpdateDescriptorSets(mDevice.Device, writes, 0, null);
        }

        void IBindableVulkanResource.Unbind(int set, int binding, int index, VulkanPipeline pipeline, nint dynamicId)
        {
            // nothing, we don't have to do any rebinding
        }

        public DeviceBufferUsage Usage => mUsage;
        public int Size => mSize;
        public Silk.NET.Vulkan.Buffer Buffer => mBuffer;

        ulong IBindableVulkanResource.ID => mID;

        private readonly VulkanDevice mDevice;
        private readonly VulkanMemoryAllocator mAllocator;

        private readonly DeviceBufferUsage mUsage;
        private readonly int mSize;
        private readonly ulong mID;
        private bool mDisposed;

        private readonly Silk.NET.Vulkan.Buffer mBuffer;
        private readonly Allocation mAllocation;
    }
}
