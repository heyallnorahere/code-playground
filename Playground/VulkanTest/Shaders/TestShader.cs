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
            public Vector3<float> Normal;
            [Layout(Location = 2)]
            public Vector2<float> UV;
        }

        public struct VertexOut
        {
            [OutputPosition]
            public Vector4<float> Position;
            public FragmentIn OutputData;
        }

        public struct FragmentIn
        {
            [Layout(Location = 0)]
            public Vector3<float> Normal;
            [Layout(Location = 1)]
            public Vector2<float> UV;
        }

        [Layout(Set = 0, Binding = 0)]
        public static Sampler2D<float>? uTexture;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            return new VertexOut
            {
                Position = new Vector4<float>(input.Position, 1f),
                OutputData = new FragmentIn
                {
                    Normal = input.Normal.Normalize(),
                    UV = input.UV
                }
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain(FragmentIn input)
        {
            return uTexture!.Sample(input.UV);
        }
    }
}