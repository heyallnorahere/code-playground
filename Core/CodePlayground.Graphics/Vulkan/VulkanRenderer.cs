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

        public void SetScissor(VulkanCommandBuffer commandBuffer, int index, int x, int y, int width, int height)
        {
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

        public void RenderIndexed(VulkanCommandBuffer commandBuffer, int indexOffset, int indexCount)
        {
            var api = VulkanContext.API;
            api.CmdDrawIndexed(commandBuffer.Buffer, (uint)indexCount, 1, (uint)indexOffset, 0, 0);
        }
    }
}
