using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace VulkanTest.Shaders
{
    [CompiledShader]
    public sealed class TestShader : ShaderBase
    {
        [ShaderEntrypoint(ShaderStage.Vertex)]
        public float VertexMain()
        {
            return 1f;
        }
    }
}