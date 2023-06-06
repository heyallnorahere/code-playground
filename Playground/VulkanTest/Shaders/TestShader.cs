using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;
using System;
using System.Runtime.CompilerServices;

namespace VulkanTest.Shaders
{
    // temporary type
    [PrimitiveShaderType("mat4")]
    public sealed class Matrix4x4<T> where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector4<T> operator *(Matrix4x4<T> lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Matrix4x4<T> operator *(Matrix4x4<T> lhs, Matrix4x4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("transpose")]
        public Matrix4x4<T> Transpose()
        {
            throw new NotImplementedException();
        }
    }

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
            [Layout(Location = 3)]
            public int Transform;
        }

        public struct VertexOut
        {
            [OutputPosition]
            public Vector4<float> Position;
            public FragmentIn Data;
        }

        public struct FragmentIn
        {
            [Layout(Location = 0)]
            public Vector3<float> Normal;
            [Layout(Location = 1)]
            public Vector2<float> UV;
        }

        public struct UniformBufferData
        {
            public Matrix4x4<float> ModelViewProjection;
        }

        [Layout(Set = 0, Binding = 0)]
        public static UniformBufferData u_UniformBuffer;

        [Layout(Set = 0, Binding = 1)]
        public static Sampler2D<float>? u_Texture;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            var vertexPosition = new Vector4<float>(input.Position, 1f);

            Vector4<float> outputPosition;
            if (input.Transform == 1)
            {
                outputPosition = u_UniformBuffer.ModelViewProjection * vertexPosition;
            }
            else
            {
                outputPosition = vertexPosition;
            }

            return new VertexOut
            {
                Position = outputPosition,
                Data = new FragmentIn
                {
                    Normal = input.Normal,
                    UV = input.UV
                },
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain(FragmentIn input)
        {
            var normalColor = new Vector4<float>(input.Normal, 1f);
            var textureColor = u_Texture!.Sample(input.UV);
            return BuiltinFunctions.Lerp(normalColor, textureColor, 0.5f);
        }
    }
}