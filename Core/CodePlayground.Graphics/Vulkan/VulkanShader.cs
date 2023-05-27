using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using Vortice.ShaderCompiler;

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

    internal sealed class VulkanShaderCompiler : IShaderCompiler
    {
        public VulkanShaderCompiler(Version vulkanVersion)
        {
            uint version = VulkanUtilities.MakeVersion(vulkanVersion);

            mDisposed = false;
            mCompiler = new Compiler();

            mCompiler.Options.SetTargetEnv(TargetEnvironment.Vulkan, version);
            mCompiler.Options.SetargetSpirv(SpirVVersion.Version_1_0);

#if DEBUG
            mCompiler.Options.SetGenerateDebugInfo();
            mCompiler.Options.SetOptimizationLevel(OptimizationLevel.Zero);
#else
            mCompiler.Options.SetOptimizationLevel(OptimizationLevel.Performance);
#endif
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            mCompiler.Dispose();
            mDisposed = true;
        }

        public byte[] Compile(string source, string path, ShaderLanguage language, ShaderStage stage, string entrypoint)
        {
            lock (mCompiler)
            {
                mCompiler.Options.SetSourceLanguage(language switch
                {
                    ShaderLanguage.GLSL => SourceLanguage.GLSL,
                    ShaderLanguage.HLSL => SourceLanguage.HLSL,
                    _ => throw new ArgumentException("Invalid shader language!")
                });

                var kind = stage switch
                {
                    ShaderStage.Vertex => ShaderKind.VertexShader,
                    ShaderStage.Fragment => ShaderKind.FragmentShader,
                    _ => throw new ArgumentException("Unsupported shader stage!")
                };

                using var result = mCompiler.Compile(source, path, kind, entryPoint: entrypoint);
                if (result.Status != CompilationStatus.Success)
                {
                    throw new InvalidOperationException($"Failed to compile shader ({result.Status}): {result.ErrorMessage}");
                }

                var bytecode = result.GetBytecode();
                var spirv = new byte[bytecode.Length];

                bytecode.CopyTo(spirv);
                return spirv;
            }
        }

        public ShaderLanguage PreferredLanguage => ShaderLanguage.GLSL;

        private readonly Compiler mCompiler;
        private bool mDisposed;
    }
}
