using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace VulkanTest.Shaders
{
    [CompiledShader]
    public sealed class TestCompute
    {
        public const int BlockSize = 16;

        public struct Input
        {
            [ShaderVariable(ShaderVariableID.GlobalInvocationID)]
            public Vector3<uint> GlobalInvocationID;
        }

        [Layout(Set = 0, Binding = 0, Format = ShaderImageFormat.RGBA8)]
        public static Image2D<float>? u_Input;

        [Layout(Set = 0, Binding = 1, Format = ShaderImageFormat.RGBA8)]
        public static Image2D<float>? u_Result;

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(BlockSize, BlockSize, 1)]
        public static void Entrypoint(Input input)
        {
            var position = new Vector2<int>((int)input.GlobalInvocationID.X, (int)input.GlobalInvocationID.Y);

            // edge-detection kernel
            // https://github.com/SaschaWillems/Vulkan/blob/master/shaders/glsl/computeshader/edgedetect.comp
            var kernel = new Matrix3x3<float>(new Vector3<float>(-1f / 8f, -1f / 8f, -1f / 8f),
                                              new Vector3<float>(-1f / 8f, 1f, -1f / 8f),
                                              new Vector3<float>(-1f / 8f, -1f / 8f, -1f / 8f)).Transpose();

            var data = new Matrix3x3<float>(0f);
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    var offset = new Vector2<int>(x, y);
                    var source = u_Input!.Load(position + offset);
                    data[x][y] = (source.R + source.G + source.B) / 3f;
                }
            }

            var result = ShaderUtilities.ApplyKernel(kernel, data, 1f, 0.5f);
            var resultColor = new Vector4<float>(new Vector3<float>(result), 1f);

            u_Result!.Store(position, resultColor);
        }
    }
}
