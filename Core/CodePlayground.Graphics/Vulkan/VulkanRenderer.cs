using Silk.NET.Vulkan;
using System;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanRenderer : IRenderer
    {
        void IRenderer.SetScissor(ICommandList commandList, int index, int x, int y, int width, int height)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer!");
            }

            SetScissor((VulkanCommandBuffer)commandList, index, x, y, width, height);
        }

        public static void SetScissor(VulkanCommandBuffer commandBuffer, int index, int x, int y, int width, int height)
        {
            using var setScissorEvent = Profiler.Event();

            var api = VulkanContext.API;
            api.CmdSetScissor(commandBuffer.Buffer, (uint)index, 1, new Rect2D
            {
                Offset = new Offset2D
                {
                    X = x,
                    Y = y
                },
                Extent = new Extent2D
                {
                    Width = (uint)width,
                    Height = (uint)height
                }
            });
        }

        void IRenderer.RenderIndexed(ICommandList commandList, int indexOffset, int indexCount)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer!");
            }

            RenderIndexed((VulkanCommandBuffer)commandList, indexOffset, indexCount);
        }

        public static void RenderIndexed(VulkanCommandBuffer commandBuffer, int indexOffset, int indexCount)
        {
            using var renderEvent = Profiler.Event();

            var api = VulkanContext.API;
            api.CmdDrawIndexed(commandBuffer.Buffer, (uint)indexCount, 1, (uint)indexOffset, 0, 0);
        }

        void IRenderer.DispatchCompute(ICommandList commandList, int x, int y, int z)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer!");
            }

            DispatchCompute((VulkanCommandBuffer)commandList, x, y, z);
        }

        public static void DispatchCompute(VulkanCommandBuffer commandBuffer, int x, int y, int z)
        {
            using var computeEvent = Profiler.Event();

            var api = VulkanContext.API;
            api.CmdDispatch(commandBuffer.Buffer, (uint)x, (uint)y, (uint)z);
        }
    }
}
