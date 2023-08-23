using System;
using System.Runtime.CompilerServices;

namespace CodePlayground.Graphics.Shaders
{
    // todo: generate from the v450 glsl standard
    public static class BuiltinFunctions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("mix")]
        public static float Lerp(float a, float b, float t)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("mix")]
        public static T Lerp<T>(T a, T b, float t) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("length")]
        public static float Length<T>(this T vector) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("normalize")]
        public static T Normalize<T>(this T vector) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("dot")]
        public static float Dot<T>(T lhs, T rhs) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("clamp")]
        public static float Clamp(float value, float min, float max)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction("discard", Keyword = true)]
        public static void Discard()
        {
            throw new NotImplementedException();
        }
    }

    [PrimitiveShaderType("vec2")]
    public class Vector2<T> where T : unmanaged
    {
        public Vector2(T scalar)
        {
            throw new NotImplementedException();
        }

        public Vector2(T x, T y)
        {
            throw new NotImplementedException();
        }

        [ShaderFieldName("x")]
        public T X;
        [ShaderFieldName("y")]
        public T Y;

        [ShaderFieldName("r")]
        public T R;
        [ShaderFieldName("g")]
        public T G;

        #region Operators
        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector2<T> operator +(Vector2<T> lhs, Vector2<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector2<T> operator +(Vector2<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector2<T> operator +(T lhs, Vector2<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Invert)]
        public static Vector2<T> operator -(Vector2<T> vector)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Subtract)]
        public static Vector2<T> operator -(Vector2<T> lhs, Vector2<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Subtract)]
        public static Vector2<T> operator -(Vector2<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector2<T> operator *(Vector2<T> lhs, Vector2<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector2<T> operator *(Vector2<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector2<T> operator *(T lhs, Vector2<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector2<T> operator /(Vector2<T> lhs, Vector2<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector2<T> operator /(Vector2<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector2<T> operator /(T lhs, Vector2<T> rhs)
        {
            throw new NotImplementedException();
        }

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            [ShaderOperator(ShaderOperatorType.Index)]
            get => throw new NotImplementedException();

            [MethodImpl(MethodImplOptions.NoInlining)]
            [ShaderOperator(ShaderOperatorType.Index)]
            set => throw new NotImplementedException();
        }
        #endregion

        // todo: swizzles
    }

    [PrimitiveShaderType("vec3")]
    public class Vector3<T> : Vector2<T> where T : unmanaged
    {
        public Vector3(T scalar) : base(scalar)
        {
            throw new NotImplementedException();
        }

        public Vector3(T x, T y, T z) : base(x, y)
        {
            throw new NotImplementedException();
        }

        public Vector3(Vector2<T> xy, T z) : base(xy.X, xy.Y)
        {
            throw new NotImplementedException();
        }

        public Vector3(T x, Vector2<T> yz) : base(x, yz.X)
        {
            throw new NotImplementedException();
        }

        [ShaderFieldName("z")]
        public T Z;
        [ShaderFieldName("b")]
        public T B;

        #region Operators
        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector3<T> operator +(Vector3<T> lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector3<T> operator +(Vector3<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector3<T> operator +(T lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Invert)]
        public static Vector3<T> operator -(Vector3<T> vector)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Subtract)]
        public static Vector3<T> operator -(Vector3<T> lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Subtract)]
        public static Vector3<T> operator -(Vector3<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector3<T> operator *(Vector3<T> lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector3<T> operator *(Vector3<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector3<T> operator *(T lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector3<T> operator /(Vector3<T> lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector3<T> operator /(Vector3<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector3<T> operator /(T lhs, Vector3<T> rhs)
        {
            throw new NotImplementedException();
        }
        #endregion

        // todo: swizzles
    }

    [PrimitiveShaderType("vec4")]
    public class Vector4<T> : Vector3<T> where T : unmanaged
    {
        public Vector4(T scalar) : base(scalar)
        {
            throw new NotImplementedException();
        }

        public Vector4(T x, T y, T z, T w) : base(x, y, z)
        {
            throw new NotImplementedException();
        }

        public Vector4(Vector3<T> xyz, T w) : base(xyz.X, xyz.Y, xyz.Z)
        {
            throw new NotImplementedException();
        }

        public Vector4(T x, Vector3<T> yzw) : base(x, yzw.X, yzw.Y)
        {
            throw new NotImplementedException();
        }

        public Vector4(Vector2<T> xy, Vector2<T> zw) : base(xy.X, xy.Y, zw.X)
        {
            throw new NotImplementedException();
        }

        public Vector4(Vector2<T> xy, T z, T w) : base(xy.X, xy.Y, z)
        {
            throw new NotImplementedException();
        }

        public Vector4(T x, T y, Vector2<T> zw) : base(x, y, zw.X)
        {
            throw new NotImplementedException();
        }

        [ShaderFieldName("w")]
        public T W;
        [ShaderFieldName("a")]
        public T A;

        #region Operators
        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector4<T> operator +(Vector4<T> lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector4<T> operator +(Vector4<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Vector4<T> operator +(T lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Invert)]
        public static Vector4<T> operator -(Vector4<T> vector)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Subtract)]
        public static Vector4<T> operator -(Vector4<T> lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Subtract)]
        public static Vector4<T> operator -(Vector4<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector4<T> operator *(Vector4<T> lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector4<T> operator *(Vector4<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Vector4<T> operator *(T lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector4<T> operator /(Vector4<T> lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector4<T> operator /(Vector4<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Divide)]
        public static Vector4<T> operator /(T lhs, Vector4<T> rhs)
        {
            throw new NotImplementedException();
        }
        #endregion

        // todo: swizzles
    }

    [PrimitiveShaderType("sampler2D", Instantiable = false, TypeClass = PrimitiveShaderTypeClass.Sampler)]
    public sealed class Sampler2D<T> where T : unmanaged
    {
        [BuiltinShaderFunction("texture")]
        public Vector4<T> Sample(Vector2<T> uv)
        {
            throw new NotImplementedException();
        }

        [BuiltinShaderFunction("texture")]
        public Vector4<T> Sample(Vector2<T> uv, float bias)
        {
            throw new NotImplementedException();
        }
    }

    [PrimitiveShaderType("image2D", Instantiable = false, TypeClass = PrimitiveShaderTypeClass.Image)]
    public sealed class Image2D<T> where T : unmanaged
    {
        [BuiltinShaderFunction("imageLoad")]
        public Vector4<T> Load(Vector2<int> position)
        {
            throw new NotImplementedException();
        }

        [BuiltinShaderFunction("imageStore")]
        public void Store(Vector2<int> position, Vector4<T> color)
        {
            throw new NotImplementedException();
        }
    }

    // INCOMPLETE
    [PrimitiveShaderType("mat4")]
    public sealed class Matrix4x4<T> where T : unmanaged
    {
        public Matrix4x4(T scalar)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Add)]
        public static Matrix4x4<T> operator +(Matrix4x4<T> lhs, Matrix4x4<T> rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [ShaderOperator(ShaderOperatorType.Multiply)]
        public static Matrix4x4<T> operator *(Matrix4x4<T> lhs, T rhs)
        {
            throw new NotImplementedException();
        }

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

    // INCOMPLETE
    [PrimitiveShaderType("mat3")]
    public sealed class Matrix3x3<T> where T : unmanaged
    {
        public Matrix3x3(T scalar)
        {
            throw new NotImplementedException();
        }

        public Matrix3x3(Vector3<T> col1, Vector3<T> col2, Vector3<T> col3)
        {
            throw new NotImplementedException();
        }

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

        public Vector3<T> this[int index]
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            [ShaderOperator(ShaderOperatorType.Index)]
            get => throw new NotImplementedException();

            [MethodImpl(MethodImplOptions.NoInlining)]
            [ShaderOperator(ShaderOperatorType.Index)]
            set => throw new NotImplementedException();
        }
    }

    // todo: implement more types
}