using Silk.NET.Vulkan;

namespace CodePlayground.Graphics.Vulkan
{
    public sealed class VulkanShader : IShader
    {
        public VulkanShader(VulkanDevice device, byte[] data, ShaderStage stage, string entrypoint)
        {
            mDevice = device;
            mStage = stage;
            mEntrypoint = entrypoint;
            mDisposed = false;

            Load(data);
        }

        ~VulkanShader()
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
            api.DestroyShaderModule(mDevice.Device, mModule, null);
        }

        private unsafe void Load(byte[] data)
        {
            fixed (byte* ptr = data)
            {
                var createInfo = VulkanUtilities.Init<ShaderModuleCreateInfo>() with
                {
                    Flags = ShaderModuleCreateFlags.None,
                    CodeSize = (nuint)data.Length,
                    PCode = (uint*)ptr
                };

                fixed (ShaderModule* module = &mModule)
                {
                    var api = VulkanContext.API;
                    api.CreateShaderModule(mDevice.Device, &createInfo, null, module);
                }
            }
        }

        public ShaderStage Stage => mStage;
        public string Entrypoint => mEntrypoint;
        public ShaderModule Module => mModule;

        private readonly VulkanDevice mDevice;
        private ShaderModule mModule;

        private readonly ShaderStage mStage;
        private readonly string mEntrypoint;
        private bool mDisposed;
    }
}
