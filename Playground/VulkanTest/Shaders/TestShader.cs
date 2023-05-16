using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;
using System.Runtime.CompilerServices;

namespace VulkanTest.Shaders
{
    [CompiledShader]
    public sealed class TestShader : ShaderBase
    {
        public struct VertexIn
        {
            public Vector3<float> Position;
            public Vector3<float> Normal;
            public Vector2<float> UV;
        }

        public struct VertexOut
        {
            public Vector4<float> Position;
            public FragmentIn OutputData;
        }

        public struct FragmentIn
        {
            [ShaderLocation(0)]
            public Vector3<float> Normal;
            [ShaderLocation(1)]
            public Vector2<float> UV;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private float Add(float lhs, float rhs)
        {
            return lhs + rhs;
        }

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public VertexOut VertexMain(VertexIn input)
        {
            return new VertexOut
            {
                Position = new Vector4<float>(input.Position, 1f),
                OutputData = new FragmentIn
                {
                    Normal = Normalize(input.Normal),
                    UV = input.UV
                }
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: ShaderLocation(0)]
        public Vector4<float> FragmentMain(FragmentIn input)
        {
            return new Vector4<float>(0f, 1f, 0f, 1f);
        }
    }
}