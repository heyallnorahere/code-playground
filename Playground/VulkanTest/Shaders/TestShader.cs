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
    }

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

        public struct UniformBufferData
        {
            public Matrix4x4<float> ViewProjection, Model;
        }

        [Layout(Set = 0, Binding = 0)]
        public static UniformBufferData u_UniformBuffer;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            var vertexPosition = new Vector4<float>(input.Position, 1f);
            var worldPosition = u_UniformBuffer.Model * vertexPosition;

            return new VertexOut
            {
                Position = u_UniformBuffer.ViewProjection * worldPosition,
                Color = input.Color,
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain([Layout(Location = 0)] Vector3<float> color)
        {
            return new Vector4<float>(color, color.X);
        }
    }
}