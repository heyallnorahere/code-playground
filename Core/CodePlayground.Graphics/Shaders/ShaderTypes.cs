using System;

namespace CodePlayground.Graphics.Shaders
{
    public abstract class ShaderBase
    {
        [BuiltinShaderFunction("length")]
        public float Length(Vector2<float> vector)
        {
            throw new NotImplementedException();
        }

        [BuiltinShaderFunction("normalize")]
        public Vector2<float> Normalize(Vector2<float> vector)
        {
            throw new NotImplementedException();
        }

        [BuiltinShaderFunction("normalize")]
        public Vector3<float> Normalize(Vector3<float> vector)
        {
            throw new NotImplementedException();
        }

        [BuiltinShaderFunction("normalize")]
        public Vector4<float> Normalize(Vector4<float> vector)
        {
            throw new NotImplementedException();
        }
    }

    [PrimitiveShaderType("vec2", IsGeneric = true)]
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
    }

    [PrimitiveShaderType("vec3", IsGeneric = true)]
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
    }

    [PrimitiveShaderType("vec4", IsGeneric = true)]
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

        public T W { get; set; }
        public T A { get; set; }
    }
}