using System;
using System.Runtime.CompilerServices;

namespace CodePlayground.Graphics.Shaders
{
    // todo: generate from the v450 glsl standard
    public static class BuiltinFunctions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("mix", Language = ShaderLanguage.GLSL)]
        public static float Lerp(float a, float b, float t)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("mix", Language = ShaderLanguage.GLSL)]
        public static T Lerp<T>(T a, T b, float t) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("length", Language = ShaderLanguage.GLSL)]
        public static float Length<T>(this T vector) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("normalize", Language = ShaderLanguage.GLSL)]
        public static T Normalize<T>(this T vector) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("dot", Language = ShaderLanguage.GLSL)]
        public static float Dot<T>(T lhs, T rhs) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("cross", Language = ShaderLanguage.GLSL)]
        public static T Cross<T>(T lhs, T rhs) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("max", Language = ShaderLanguage.GLSL)]
        public static T Max<T>(T lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("min", Language = ShaderLanguage.GLSL)]
        public static T Min<T>(T lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("clamp", Language = ShaderLanguage.GLSL)]
        public static float Clamp(float value, float min, float max)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction(Keyword = true)]
        [NamedShaderSymbol("discard", Language = ShaderLanguage.GLSL)]
        public static void Discard()
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("barrier", Language = ShaderLanguage.GLSL)]
        public static void Barrier()
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("groupMemoryBarrier", Language = ShaderLanguage.GLSL)]
        public static void GroupMemoryBarrier()
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("pow", Language = ShaderLanguage.GLSL)]
        public static float Pow(float x, float y)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("exp", Language = ShaderLanguage.GLSL)]
        public static float Exp(float x)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("tanh", Language = ShaderLanguage.GLSL)]
        public static float Tanh(float x)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("reflect", Language = ShaderLanguage.GLSL)]
        public static T Reflect<T>(T x, T y) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("EmitVertex", Language = ShaderLanguage.GLSL)]
        public static void EmitVertex()
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("EndPrimitive", Language = ShaderLanguage.GLSL)]
        public static void EndPrimitive()
        {
            throw new NotImplementedException();
        }
    }

    // specifically atomic operations
    // takes first argument by reference when translated due to the quirks of the glsl transpiler
    public static class Atomic
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("atomicAdd", Language = ShaderLanguage.GLSL)]
        public static T Add<T>(T lhs, T rhs)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("atomicExchange", Language = ShaderLanguage.GLSL)]
        public static T Exchange<T>(T lhs, T rhs)
        {
            throw new NotImplementedException();
        }
    }

    [PrimitiveShaderType]
    [NamedShaderSymbol("vec2", Language = ShaderLanguage.GLSL)]
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

        [NamedShaderSymbol("x")]
        public T X;
        [NamedShaderSymbol("y")]
        public T Y;

        [NamedShaderSymbol("r")]
        public T R;
        [NamedShaderSymbol("g")]
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

    [PrimitiveShaderType]
    [NamedShaderSymbol("vec3", Language = ShaderLanguage.GLSL)]
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

        [NamedShaderSymbol("z")]
        public T Z;
        [NamedShaderSymbol("b")]
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

    [PrimitiveShaderType]
    [NamedShaderSymbol("vec4", Language = ShaderLanguage.GLSL)]
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

        [NamedShaderSymbol("w")]
        public T W;
        [NamedShaderSymbol("a")]
        public T A;

        // todo: swizzling
        [NamedShaderSymbol("xyz", Language = ShaderLanguage.GLSL)]
        public Vector3<T> XYZ;

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

    [PrimitiveShaderType(Instantiable = false, TypeClass = PrimitiveShaderTypeClass.Sampler)]
    [NamedShaderSymbol("sampler2D", Language = ShaderLanguage.GLSL)]
    public sealed class Sampler2D<T> where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("texture", Language = ShaderLanguage.GLSL)]
        public Vector4<T> Sample(Vector2<T> uv)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("texture", Language = ShaderLanguage.GLSL)]
        public Vector4<T> Sample(Vector2<T> uv, float bias)
        {
            throw new NotImplementedException();
        }
    }

    [PrimitiveShaderType(Instantiable = false, TypeClass = PrimitiveShaderTypeClass.Sampler)]
    [NamedShaderSymbol("samplerCube", Language = ShaderLanguage.GLSL)]
    public sealed class SamplerCube<T> where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("texture", Language = ShaderLanguage.GLSL)]
        public Vector4<T> Sample(Vector3<T> uv)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("texture", Language = ShaderLanguage.GLSL)]
        public Vector4<T> Sample(Vector3<T> uv, float bias)
        {
            throw new NotImplementedException();
        }
    }

    [PrimitiveShaderType(Instantiable = false, TypeClass = PrimitiveShaderTypeClass.Image)]
    [NamedShaderSymbol("image2D", Language = ShaderLanguage.GLSL)]
    public sealed class Image2D<T> where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("imageLoad", Language = ShaderLanguage.GLSL)]
        public Vector4<T> Load(Vector2<int> position)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("imageStore", Language = ShaderLanguage.GLSL)]
        public void Store(Vector2<int> position, Vector4<T> color)
        {
            throw new NotImplementedException();
        }

        public Vector2<int> Size
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            [BuiltinShaderFunction]
            [NamedShaderSymbol("imageSize", Language = ShaderLanguage.GLSL)]
            get => throw new NotImplementedException();
        }
    }

    // INCOMPLETE
    [PrimitiveShaderType]
    [NamedShaderSymbol("mat4", Language = ShaderLanguage.GLSL)]
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
    [PrimitiveShaderType]
    [NamedShaderSymbol("mat3", Language = ShaderLanguage.GLSL)]
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
        [BuiltinShaderFunction]
        [NamedShaderSymbol("inverse", Language = ShaderLanguage.GLSL)]
        public Matrix3x3<T> Inverse()
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [BuiltinShaderFunction]
        [NamedShaderSymbol("transpose", Language = ShaderLanguage.GLSL)]
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