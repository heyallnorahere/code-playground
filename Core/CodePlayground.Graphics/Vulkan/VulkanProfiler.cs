using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System;
using System.Runtime.InteropServices;

using static Tracy.PInvoke;

// https://github.com/wolfpld/tracy/blob/master/public/tracy/TracyVulkan.hpp
namespace CodePlayground.Graphics.Vulkan
{
    internal sealed class VulkanProfiler : IGPUProfiler
    {
        public unsafe VulkanProfiler(VulkanDevice device)
        {
            mDevice = device;
            mDisposed = false;

            mQueryPool = CreateQueryPool(out mQueryCount);
            mCurrentQuery = mPreviousQuery = 0;
            mOldCount = 0;

            var api = VulkanContext.API;
            var queue = mDevice.GetQueue(CommandQueueFlags.Graphics);
            var commandBuffer = queue.Release();

            commandBuffer.Begin();
            api.CmdResetQueryPool(commandBuffer.Buffer, mQueryPool, 0, mQueryCount);
            api.CmdWriteTimestamp(commandBuffer.Buffer, PipelineStageFlags.TopOfPipeBit, mQueryPool, 0);
            commandBuffer.End();
            queue.Submit(commandBuffer, wait: true);

            long tgpu;
            api.GetQueryPoolResults(mDevice.Device, mQueryPool, 0, 1, sizeof(long), &tgpu, sizeof(long), QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit).Assert();

            mHostReset = api.TryGetDeviceExtension<ExtHostQueryReset>(mDevice.PhysicalDevice.Instance, mDevice.Device, out _);
            if (Application.Instance is GraphicsApplication graphicsApp && graphicsApp.GraphicsContext is VulkanContext context)
            {
                var version = context.Version;
                if (version >= new Version(1, 2, 0))
                {
                    var features12 = VulkanUtilities.Init<PhysicalDeviceVulkan12Features>();
                    var features = VulkanUtilities.Init<PhysicalDeviceFeatures2>();
                    features.SetNext(ref features12, true);

                    mDevice.PhysicalDevice.GetFeatures2(ref features);
                    mHostReset = features12.HostQueryReset;
                }
            }

            mDevice.PhysicalDevice.GetProperties(out PhysicalDeviceProperties properties);
            TracyEmitGpuNewContextSerial(new TracyGpuNewContextData
            {
                GpuTime = tgpu,
                Period = properties.Limits.TimestampPeriod,
                Context = mContext = Profiler.ContextCounter(),
                Flags = 0,
                Type = (byte)GPUProfilerContextType.Vulkan
            });

            commandBuffer = queue.Release();
            commandBuffer.Begin();

            api.CmdResetQueryPool(commandBuffer.Buffer, mQueryPool, 0, 1);
            commandBuffer.End();
            queue.Submit(commandBuffer, wait: true);
        }

        ~VulkanProfiler()
        {
            if (!mDisposed)
            {
                Dispose(false);
                mDisposed = true;
            }
        }

        public string Name
        {
            set
            {
                TracyEmitGpuContextNameSerial(new TracyGpuContextNameData
                {
                    Context = mContext,
                    _Name = Profiler.GetString(value)
                });
            }
        }

        private uint WriteTimestamp(VulkanCommandBuffer commandBuffer)
        {
            uint query = mCurrentQuery++ % mQueryCount;
            
            var api = VulkanContext.API;
            api.CmdResetQueryPool(commandBuffer.Buffer, mQueryPool, query, 1);
            api.CmdWriteTimestamp(commandBuffer.Buffer, PipelineStageFlags.BottomOfPipeBit, mQueryPool, query);

            return query;
        }

        public Action? BeginEvent(object commandList, ulong sourceLocation)
        {
            if (commandList is not VulkanCommandBuffer commandBuffer)
            {
                return null;
            }

            TracyEmitGpuZoneBeginAllocSerial(new TracyGpuZoneBeginData
            {
                Srcloc = sourceLocation,
                QueryId = (ushort)WriteTimestamp(commandBuffer),
                Context = mContext
            });

            return () => TracyEmitGpuZoneEndSerial(new TracyGpuZoneEndData
            {
                QueryId = (ushort)WriteTimestamp(commandBuffer),
                Context = mContext
            });
        }

        public void Collect()
        {
            using var collectEvent = Profiler.Event(color: ProfilerColor.Red4);
            if (mCurrentQuery == mPreviousQuery)
            {
                return;
            }

            uint wrappedTail = mPreviousQuery % mQueryCount;
            uint timestampCount;
            if (mOldCount != 0)
            {
                timestampCount = mOldCount;
                mOldCount = 0;
            }
            else
            {
                timestampCount = mCurrentQuery - mPreviousQuery;
                if (timestampCount > mQueryCount)
                {
                    throw new InvalidOperationException($"More than {mQueryCount} timestamps written in a frame!");
                }

                if (wrappedTail + timestampCount > mQueryCount)
                {
                    timestampCount = mQueryCount - wrappedTail;
                }
            }

            if (mCurrentQuery % mQueryCount - wrappedTail < timestampCount)
            {
                throw new InvalidOperationException("What happened here?");
            }

            var timestamps = new long[timestampCount];
            var api = VulkanContext.API;

            var result = api.GetQueryPoolResults(mDevice.Device, mQueryPool, wrappedTail, timestampCount, timestamps.AsSpan(), (ulong)Marshal.SizeOf<long>(), QueryResultFlags.Result64Bit);
            if (result is Result.NotReady)
            {
                mOldCount = timestampCount;
                return; // eh. it is 11:45 on new years
            }
            else
            {
                result.Assert();
            }

            for (uint i = 0; i < timestampCount; i++)
            {
                uint query = i + wrappedTail;
                TracyEmitGpuTimeSerial(new TracyGpuTimeData
                {
                    GpuTime = timestamps[i],
                    QueryId = (ushort)query,
                    Context = mContext
                });
            }

            mPreviousQuery += timestampCount;
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
            api.DestroyQueryPool(mDevice.Device, mQueryPool, null);
        }

        private unsafe QueryPool CreateQueryPool(out uint queryCount)
        {
            var poolInfo = VulkanUtilities.Init<QueryPoolCreateInfo>() with
            {
                QueryCount = queryCount = (uint)ushort.MaxValue + 1, // 2^16, ushort max
                QueryType = QueryType.Timestamp
            };

            var api = VulkanContext.API;
            QueryPool pool;

            // todo(nora): this will crash on systems that do not allow 2^16 queries on a pool (validation layer handler)
            // unfortunately there is no way to query the max query count
            while (api.CreateQueryPool(mDevice.Device, poolInfo, null, out pool) != Result.Success)
            {
                poolInfo.QueryCount = queryCount /= 2;
            }

            return pool;
        }

        private readonly VulkanDevice mDevice;
        private bool mDisposed;
        private uint mCurrentQuery, mPreviousQuery;
        private uint mOldCount;

        private readonly QueryPool mQueryPool;
        private readonly uint mQueryCount;
        private readonly byte mContext;
        private readonly bool mHostReset;
    }
}