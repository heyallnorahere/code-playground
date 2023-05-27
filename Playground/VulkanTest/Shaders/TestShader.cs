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
            float a;
            if (input.Position.X > 0.5f)
            {
                a = Add(0.3f, 0.7f);
            }
            else if (input.Position.X < 0f)
            {
                a = Add(Add(0.5f, 0.1f), Add(0.2f, 0.2f));
            }
            else
            {
                a = -Add(-0.6f, -0.4f);
            }

            const int loopCount = 7;
            float w = 0f;
            for (int i = 0; i < loopCount; i++)
            {
                w += a / loopCount;
            }

            var position = new Vector4<float>(input.Position, 0f) + new Vector4<float>(0f, 0f, 0f, w);
            return new VertexOut
            {
                Position = position,
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