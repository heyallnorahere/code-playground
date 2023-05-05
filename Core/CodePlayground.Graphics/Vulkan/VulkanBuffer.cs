﻿using Silk.NET.Vulkan;
using System;
using VMASharp;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanBuffer : IDeviceBuffer
    {
        public static BufferUsageFlags GetUsageFlags(DeviceBufferUsage usage)
        {
            return usage switch
            {
                DeviceBufferUsage.Vertex => BufferUsageFlags.VertexBufferBit,
                DeviceBufferUsage.Index => BufferUsageFlags.IndexBufferBit,
                DeviceBufferUsage.Uniform => BufferUsageFlags.UniformBufferBit,
                DeviceBufferUsage.Staging => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                _ => throw new ArgumentException("Invalid buffer usage!")
            };
        }

        public unsafe VulkanBuffer(VulkanDevice device, VulkanMemoryAllocator allocator, DeviceBufferUsage usage, int size)
        {
            mDevice = device;
            mAllocator = allocator;

            mUsage = usage;
            mSize = size;
            mDisposed = false;

            var createInfo = VulkanUtilities.Init<BufferCreateInfo>() with
            {
                Size = (ulong)size,
                Usage = GetUsageFlags(usage)
            };

            var physicalDevice = device.PhysicalDevice;
            var queueFamilies = physicalDevice.FindQueueTypes();

            int graphics = queueFamilies[CommandQueueFlags.Graphics];
            int transfer = queueFamilies[CommandQueueFlags.Transfer];

            var indices = new uint[]
            {
                (uint)graphics,
                (uint)transfer
            };

            var api = VulkanContext.API;
            fixed (uint* indexPtr = indices)
            {
                if (graphics != transfer)
                {
                    createInfo.SharingMode = SharingMode.Concurrent;
                    createInfo.QueueFamilyIndexCount = (uint)indices.Length; // 2
                    createInfo.PQueueFamilyIndices = indexPtr;
                }
                else
                {
                    createInfo.SharingMode = SharingMode.Exclusive;
                }

                fixed (Silk.NET.Vulkan.Buffer* buffer = &mBuffer)
                {
                    api.CreateBuffer(device.Device, &createInfo, null, buffer).Assert();
                }
            }

            mAllocation = allocator.AllocateMemoryForBuffer(mBuffer, new AllocationCreateInfo
            {
                Usage = usage switch
                {
                    DeviceBufferUsage.Vertex => MemoryUsage.GPU_Only,
                    DeviceBufferUsage.Index => MemoryUsage.GPU_Only,
                    DeviceBufferUsage.Uniform => MemoryUsage.CPU_To_GPU,
                    DeviceBufferUsage.Staging => MemoryUsage.CPU_To_GPU,
                    _ => throw new ArgumentException("Invalid buffer usage!")
                }
            });

            mAllocation.BindBufferMemory(mBuffer).Assert();
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

        public unsafe void CopyFromCPU(void* address, int size)
        {
            var deviceAddress = (void*)mAllocation.Map();
            System.Buffer.MemoryCopy(address, deviceAddress, mSize, size);
            mAllocation.Unmap();
        }

        public unsafe void CopyToCPU(void* address, int size)
        {
            var deviceAddress = (void*)mAllocation.Map();
            System.Buffer.MemoryCopy(deviceAddress, address, size, mSize);
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
            var api = VulkanContext.API;
            api.CmdBindVertexBuffers(commandBuffer.Buffer, (uint)index, 0, mBuffer, 0);
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
            var api = VulkanContext.API;
            api.CmdBindIndexBuffer(commandBuffer.Buffer, mBuffer, 0, indexType switch
            {
                DeviceBufferIndexType.UInt16 => IndexType.Uint16,
                DeviceBufferIndexType.UInt32 => IndexType.Uint32,
                _ => throw new ArgumentException("Invalid index type!")
            });
        }

        public DeviceBufferUsage Usage => mUsage;
        public int Size => mSize;
        public Silk.NET.Vulkan.Buffer Buffer => mBuffer;

        private readonly VulkanDevice mDevice;
        private readonly VulkanMemoryAllocator mAllocator;

        private readonly DeviceBufferUsage mUsage;
        private readonly int mSize;
        private bool mDisposed;

        private readonly Silk.NET.Vulkan.Buffer mBuffer;
        private readonly Allocation mAllocation;
    }
}