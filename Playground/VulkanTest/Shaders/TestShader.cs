using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;
using System;
using System.Runtime.CompilerServices;

namespace VulkanTest.Shaders
{
    // temporary types
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

    [PrimitiveShaderType("mat3")]
    public sealed class Matrix3x3<T> where T : unmanaged
    {
        public Matrix3x3(Matrix4x4<T> matrix)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector3<T> operator *(Matrix3x3<T> lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("inverse")]
        public Matrix3x3<T> Inverse()
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("transpose")]
        public Matrix3x3<T> Transpose()
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

        public struct CameraBufferData
        {
            public Matrix4x4<float> ViewProjection;
        }

        public struct PushConstantData
        {
            public Matrix4x4<float> Model;
            public Vector4<float> Color;
        }

        [Layout(Set = 0, Binding = 0)]
        public static CameraBufferData u_CameraBuffer;

        [Layout(PushConstants = true)]
        public static PushConstantData u_PushConstants;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            var vertexPosition = new Vector4<float>(input.Position, 1f);
            var worldPosition = u_PushConstants.Model * vertexPosition;

            return new VertexOut
            {
                Position = u_CameraBuffer.ViewProjection * worldPosition,
                Data = new FragmentIn
                {
                    Normal = (new Matrix3x3<float>(u_PushConstants.Model).Inverse().Transpose() * input.Normal).Normalize(),
                    UV = input.UV
                },
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain(FragmentIn input)
        {
            return u_PushConstants.Color;
        }
    }
}