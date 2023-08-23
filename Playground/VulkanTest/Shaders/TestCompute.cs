using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace VulkanTest.Shaders
{
    [CompiledShader]
    public static class TestCompute
    {
        public const int BlockSize = 16;

        public struct Input
        {
            [ShaderVariable(ShaderVariableID.GlobalInvocationID)]
            public Vector3<uint> GlobalInvocationID;
        }

        [Layout(Set = 0, Binding = 0, Format = ShaderImageFormat.RGBA8)]
        public static Image2D<float>? u_Result;

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(BlockSize, BlockSize, 1)]
        public static void Entrypoint(Input input)
        {
            var position = new Vector2<int>((int)input.GlobalInvocationID.X, (int)input.GlobalInvocationID.Y);
        }
    }
}
