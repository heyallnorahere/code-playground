using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;
using System.Runtime.CompilerServices;

namespace VulkanTest.Shaders
{
    [CompiledShader]
    public sealed class TestShader
    {
        public struct VertexIn
        {
            [Layout(Location = 0)]
            public Vector3<float> Position;
            [Layout(Location = 1)]
            public Vector3<float> Color;
        }

        public struct VertexOut
        {
            [OutputPosition]
            public Vector4<float> Position;
            [Layout(Location = 0)]
            public Vector3<float> Color;
        }

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            return new VertexOut
            {
                Position = new Vector4<float>(input.Position, 1f),
                Color = input.Color
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain([Layout(Location = 0)] Vector3<float> color)
        {
            return new Vector4<float>(color, 1f);
        }
    }
}