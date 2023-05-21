using System;

namespace CodePlayground.Graphics.Shaders
{
    public static class BuiltinFunctions
    {
        [BuiltinShaderFunction("length")]
        public static float Length(Vector2<float> vector)
        {
            throw new NotImplementedException();
        }

        [BuiltinShaderFunction("normalize")]
        public static T Normalize<T>(T vector) where T : Vector2<float>
        {
            throw new NotImplementedException();
        }
    }

    [PrimitiveShaderType("vec2")]
    public class Vector2<T> where T : unmanaged
    {
        public Vector2(T x, T y)
        {
            throw new NotImplementedException();
        }

        public T X { get; set; }
        public T Y { get; set; }

        public T R { get; set; }
        public T G { get; set; }

        // todo: swizzles
    }

    [PrimitiveShaderType("vec3")]
    public class Vector3<T> : Vector2<T> where T : unmanaged
    {
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

        public T Z { get; set; }
        public T B { get; set; }

        // todo: swizzles
    }

    [PrimitiveShaderType("vec4")]
    public class Vector4<T> : Vector3<T> where T : unmanaged
    {
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

        public T W { get; set; }
        public T A { get; set; }

        // todo: swizzles
    }
}