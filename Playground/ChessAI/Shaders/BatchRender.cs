using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace ChessAI.Shaders
{
    [CompiledShader]
    public sealed class BatchRender
    {
        public const int MaxTextures = 16;

        public struct VertexInput
        {
            [Layout(Location = 0)]
            public Vector2<float> Position;

            [Layout(Location = 1)]
            public Vector4<float> Color;

            [Layout(Location = 2)]
            public Vector2<float> UV;

            [Layout(Location = 3)]
            public int TextureIndex;
        }

        public struct VertexOutput
        {
            [ShaderVariable(ShaderVariableID.OutputPosition)]
            public Vector4<float> Position;

            public FragmentInput Data;
        }

        public struct FragmentInput
        {
            [Layout(Location = 0)]
            public Vector2<float> UV;

            [Layout(Location = 1)]
            public Vector4<float> Color;

            [Layout(Location = 2, Flat = true)]
            public int TextureIndex;
        }

        public struct CameraBufferData
        {
            public Matrix4x4<float> ViewProjection;
        }

        [Layout(Set = 0, Binding = 0)]
        public static CameraBufferData u_CameraBuffer;

        [Layout(Set = 0, Binding = 1)]
        [ArraySize(MaxTextures)]
        public static Sampler2D<float>[]? u_Textures;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOutput VertexEntrypoint(VertexInput input)
        {
            return new VertexOutput
            {
                Position = u_CameraBuffer.ViewProjection * new Vector4<float>(input.Position, 0f, 1f),
                Data = new FragmentInput
                {
                    UV = input.UV,
                    Color = input.Color,
                    TextureIndex = input.TextureIndex
                }
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentEntrypoint(FragmentInput input)
        {
            var textureColor = u_Textures![input.TextureIndex].Sample(input.UV);
            return textureColor * input.Color;
        }
    }
}
