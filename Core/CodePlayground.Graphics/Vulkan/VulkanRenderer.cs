using System;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanRenderer : IRenderer
    {
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
