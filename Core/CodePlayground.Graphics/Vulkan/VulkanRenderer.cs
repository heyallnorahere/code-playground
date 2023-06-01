using System;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanRenderer : IRenderer
    {
        void IRenderer.RenderIndexed(ICommandList commandBuffer, int indexCount)
        {
            if (commandBuffer is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer!");
            }

            RenderIndexed((VulkanCommandBuffer)commandBuffer, indexCount);
        }

        public void RenderIndexed(VulkanCommandBuffer commandBuffer, int indexCount)
        {
            var api = VulkanContext.API;
            api.CmdDrawIndexed(commandBuffer.Buffer, (uint)indexCount, 1, 0, 0, 0);
        }
    }
}
