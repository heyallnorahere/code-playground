using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanCommandBuffer : ICommandList, IDisposable
    {
        public VulkanCommandBuffer(CommandPool pool, Device device)
        {
            mDisposed = false;
            mRecording = false;

            mPool = pool;
            mDevice = device;

            var allocInfo = VulkanUtilities.Init<CommandBufferAllocateInfo>() with
            {
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            var api = VulkanContext.API;
            api.AllocateCommandBuffers(device, allocInfo, out mBuffer).Assert();
        }

        ~VulkanCommandBuffer()
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

        private void Dispose(bool disposing)
        {
            var api = VulkanContext.API;
            api.FreeCommandBuffers(mDevice, mPool, 1, mBuffer);
        }

        public void Begin()
        {
            var beginInfo = VulkanUtilities.Init<CommandBufferBeginInfo>();
            var api = VulkanContext.API;
            api.BeginCommandBuffer(mBuffer, beginInfo).Assert();
        }

        public void End()
        {
            var api = VulkanContext.API;
            api.EndCommandBuffer(mBuffer).Assert();
        }

        public bool IsRecording => mRecording;
        public CommandBuffer Buffer => mBuffer;

        private bool mDisposed, mRecording;
        private readonly Device mDevice;
        private readonly CommandPool mPool;
        private readonly CommandBuffer mBuffer;
    }

    internal struct StoredCommandBuffer
    {
        public VulkanCommandBuffer CommandBuffer { get; set; }
        public Fence Fence { get; set; }
        public bool OwnsFence { get; set; }
    }

    public struct VulkanQueueSemaphoreDependency
    {
        public Semaphore Semaphore { get; set; }
        public PipelineStageFlags DestinationStageMask { get; set; }
    }

    public struct VulkanQueueSubmitInfo
    {
        public Fence? Fence { get; set; }
        public IReadOnlyList<VulkanQueueSemaphoreDependency>? WaitSemaphores { get; set; }
        public IReadOnlyList<Semaphore>? SignalSemaphores { get; set; }
    }

    public sealed class VulkanQueue : ICommandQueue, IDisposable
    {
        internal unsafe VulkanQueue(int queueFamily, CommandQueueFlags usage, VulkanDevice device)
        {
            var api = VulkanContext.API;
            mDevice = device.Device;
            mQueue = api.GetDeviceQueue(mDevice, (uint)queueFamily, 0);

            mStoredBuffers = new Queue<StoredCommandBuffer>();
            mFences = new Queue<Fence>();

            mUsage = usage;
            mBufferCap = -1;
            mDisposed = false;

            var createInfo = VulkanUtilities.Init<CommandPoolCreateInfo>() with
            {
                Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = (uint)queueFamily
            };

            fixed (CommandPool* ptr = &mPool)
            {
                api.CreateCommandPool(mDevice, &createInfo, null, ptr).Assert();
            }
        }

        ~VulkanQueue()
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
            ClearCache();

            var api = VulkanContext.API;
            while (mFences.Count > 0)
            {
                var fence = mFences.Dequeue();
                api.DestroyFence(mDevice, fence, null);
            }

            api.DestroyCommandPool(mDevice, mPool, null);
        }

        ICommandList ICommandQueue.Release() => Release();
        public VulkanCommandBuffer Release()
        {
            if (mStoredBuffers.Count > 0)
            {
                if (mBufferCap >= 0 && mStoredBuffers.Count > mBufferCap)
                {
                    var front = mStoredBuffers.Peek();
                    WaitFence(front.Fence);
                }

                var buffer = mStoredBuffers.Dequeue();
                var fence = buffer.Fence;
                var commandBuffer = buffer.CommandBuffer;

                var api = VulkanContext.API;
                var fenceStatus = api.GetFenceStatus(mDevice, buffer.Fence);
                if (fenceStatus == Result.Success)
                {
                    if (buffer.OwnsFence)
                    {
                        api.ResetFences(mDevice, 1, fence);
                        mFences.Enqueue(fence);
                    }

                    api.ResetCommandBuffer(commandBuffer.Buffer, CommandBufferResetFlags.None);
                    return buffer.CommandBuffer;
                }
            }

            return new VulkanCommandBuffer(mPool, mDevice);
        }

        void ICommandQueue.Submit(ICommandList commandList, bool wait)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a vulkan command buffer!");
            }

            var commandBuffer = (VulkanCommandBuffer)commandList;
            Submit(commandBuffer, wait: wait);
        }

        private unsafe Fence GetFence()
        {
            Fence fence;
            if (mFences.Count > 0)
            {
                fence = mFences.Dequeue();
            }
            else
            {
                var fenceInfo = VulkanUtilities.Init<FenceCreateInfo>() with
                {
                    Flags = FenceCreateFlags.None
                };

                var api = VulkanContext.API;
                api.CreateFence(mDevice, &fenceInfo, null, &fence).Assert();
            }

            return fence;
        }

        public unsafe void Submit(VulkanCommandBuffer commandBuffer, VulkanQueueSubmitInfo info = default, bool wait = false)
        {
            if (commandBuffer.IsRecording)
            {
                commandBuffer.End();
            }

            var waitSemaphores = info.WaitSemaphores?.Select(dependency => dependency.Semaphore).ToArray();
            var waitStages = info.WaitSemaphores?.Select(dependency => dependency.DestinationStageMask).ToArray();
            var signalSemaphores = info.SignalSemaphores?.ToArray();

            var buffer = commandBuffer.Buffer;
            var fence = info.Fence ?? GetFence();

            var api = VulkanContext.API;
            fixed (Semaphore* waitSemaphorePtr = waitSemaphores)
            {
                fixed (PipelineStageFlags* waitStagePtr = waitStages)
                {
                    fixed (Semaphore* signalSemaphorePtr = signalSemaphores)
                    {
                        var submitInfo = VulkanUtilities.Init<SubmitInfo>() with
                        {
                            WaitSemaphoreCount = (uint)(info.WaitSemaphores?.Count ?? 0),
                            PWaitSemaphores = waitSemaphorePtr,
                            PWaitDstStageMask = waitStagePtr,
                            CommandBufferCount = 1,
                            PCommandBuffers = &buffer,
                            SignalSemaphoreCount = (uint)(info.SignalSemaphores?.Count ?? 0),
                            PSignalSemaphores = signalSemaphorePtr,
                        };

                        api.QueueSubmit(mQueue, 1, submitInfo, fence).Assert();
                    }
                }
            }

            mStoredBuffers.Enqueue(new StoredCommandBuffer
            {
                CommandBuffer = commandBuffer,
                Fence = fence,
                OwnsFence = info.Fence is null
            });

            if (wait)
            {
                api.WaitForFences(mDevice, 1, fence, true, ulong.MaxValue).Assert();
            }
        }

        private void WaitFence(Fence fence)
        {
            var api = VulkanContext.API;
            api.WaitForFences(mDevice, 1, fence, true, ulong.MaxValue).Assert();
        }

        public void Wait()
        {
            var api = VulkanContext.API;
            api.QueueWaitIdle(mQueue).Assert();
        }

        public unsafe void ClearCache()
        {
            Wait();

            var api = VulkanContext.API;
            while (mStoredBuffers.Count > 0)
            {
                var storedBuffer = mStoredBuffers.Dequeue();
                storedBuffer.CommandBuffer.Dispose();

                if (storedBuffer.OwnsFence)
                {
                    api.DestroyFence(mDevice, storedBuffer.Fence, null);
                }
            }
        }

        public CommandQueueFlags Usage => mUsage;
        public Queue Queue => mQueue;
        public int CommandListCap
        {
            get => mBufferCap;
            set
            {
                mBufferCap = value;
                ClearCache();
            }
        }

        private readonly Queue mQueue;
        private readonly Device mDevice;
        private readonly CommandPool mPool;

        private readonly Queue<StoredCommandBuffer> mStoredBuffers;
        private readonly Queue<Fence> mFences;

        private readonly CommandQueueFlags mUsage;
        private int mBufferCap;
        private bool mDisposed;
    }
}
