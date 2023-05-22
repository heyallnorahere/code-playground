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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private float Add(float lhs, float rhs)
        {
            return lhs + rhs;
        }

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public VertexOut VertexMain(VertexIn input)
        {
            float w;
            if (input.Position.X > 0.5f)
            {
                w = Add(0.3f, 0.7f);
            }
            else
            {
                w = Add(0.6f, 0.4f);
            }

            return new VertexOut
            {
                Position = new Vector4<float>(input.Position, w),
                OutputData = new FragmentIn
                {
                    Normal = BuiltinFunctions.Normalize(input.Normal),
                    UV = input.UV
                }
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public Vector4<float> FragmentMain(FragmentIn input)
        {
            return new Vector4<float>(0f, 1f, 0f, 1f);
        }
    }
}