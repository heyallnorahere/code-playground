using System;
using System.Numerics;

namespace CodePlayground.Graphics
{
    public sealed class MatrixMath
    {
        public delegate Matrix4x4 PerspectiveFunction(float fov, float aspectRatio, float nearPlane, float farPlane);
        public delegate Matrix4x4 OrthographicFunction(float left, float right, float bottom, float top, float nearPlane, float farPlane);
        public delegate Matrix4x4 LookAtFunction(Vector3 eye, Vector3 center, Vector3 up);

        public MatrixMath(IGraphicsContext context)
        {
            mLeftHanded = context.LeftHanded;
            mMinDepth = context.MinDepth;
        }

        public MatrixMath(bool leftHanded, MinimumDepth minDepth)
        {
            mLeftHanded = leftHanded;
            mMinDepth = minDepth;
        }

        #region Perspective

        // https://github.com/g-truc/glm/blob/efec5db081e3aad807d0731e172ac597f6a39447/glm/ext/matrix_clip_space.inl#L265
        public static Matrix4x4 Perspective_LH_Z0(float fov, float aspectRatio, float nearPlane, float farPlane)
        {
            float g = 1f / MathF.Tan(fov / 2f);
            float k = farPlane / (farPlane - nearPlane);

            return new Matrix4x4(g / aspectRatio, 0f, 0f, 0f,
                                 0f, g, 0f, 0f,
                                 0f, 0f, k, -nearPlane * k,
                                 0f, 0f, 1f, 0f);
        }

        public static Matrix4x4 Perspective_LH_N0(float fov, float aspectRatio, float nearPlane, float farPlane)
        {
            throw new NotImplementedException();
        }

        public static Matrix4x4 Perspective_RH_Z0(float fov, float aspectRatio, float nearPlane, float farPlane)
        {
            throw new NotImplementedException();
        }

        public static Matrix4x4 Perspective_RH_N0(float fov, float aspectRatio, float nearPlane, float farPlane)
        {
            throw new NotImplementedException();
        }

        public PerspectiveFunction Perspective_LH => mMinDepth == MinimumDepth.Zero ? Perspective_LH_Z0 : Perspective_LH_N0;
        public PerspectiveFunction Perspective_RH => mMinDepth == MinimumDepth.Zero ? Perspective_RH_Z0 : Perspective_RH_N0;
        public PerspectiveFunction Perspective_Z0 => mLeftHanded ? Perspective_LH_Z0 : Perspective_RH_Z0;
        public PerspectiveFunction Perspective_N0 => mLeftHanded ? Perspective_LH_N0 : Perspective_RH_N0;
        public PerspectiveFunction Perspective => mLeftHanded ? Perspective_LH : Perspective_RH;

        #endregion
        #region Orthographic

        public static Matrix4x4 Orthographic_LH_Z0(float left, float right, float bottom, float top, float nearPlane, float farPlane)
        {
            return new Matrix4x4(2f / (right - left), 0f, 0f, -(right + left) / (right - left),
                     0f, 2f / (top - bottom), 0f, -(top + bottom) / (top - bottom),
                     0f, 0f, 1f / (farPlane - nearPlane), -nearPlane / (farPlane - nearPlane),
                     0f, 0f, 0f, 1f);
        }

        public static Matrix4x4 Orthographic_LH_N0(float left, float right, float bottom, float top, float nearPlane, float farPlane)
        {
            throw new NotImplementedException();
        }

        public static Matrix4x4 Orthographic_RH_Z0(float left, float right, float bottom, float top, float nearPlane, float farPlane)
        {
            throw new NotImplementedException();
        }

        public static Matrix4x4 Orthographic_RH_N0(float left, float right, float bottom, float top, float nearPlane, float farPlane)
        {
            throw new NotImplementedException();
        }

        public OrthographicFunction Orthographic_LH => mMinDepth == MinimumDepth.Zero ? Orthographic_LH_Z0 : Orthographic_LH_N0;
        public OrthographicFunction Orthographic_RH => mMinDepth == MinimumDepth.Zero ? Orthographic_RH_Z0 : Orthographic_RH_N0;
        public OrthographicFunction Orthographic_Z0 => mLeftHanded ? Orthographic_LH_Z0 : Orthographic_RH_Z0;
        public OrthographicFunction Orthographic_N0 => mLeftHanded ? Orthographic_LH_N0 : Orthographic_RH_N0;
        public OrthographicFunction Orthographic => mLeftHanded ? Orthographic_LH : Orthographic_RH;

        #endregion
        #region LookAt

        // https://github.com/g-truc/glm/blob/efec5db081e3aad807d0731e172ac597f6a39447/glm/ext/matrix_transform.inl#L176
        public static Matrix4x4 LookAt_LH(Vector3 eye, Vector3 center, Vector3 up)
        {
            var direction = Vector3.Normalize(center - eye);
            var right = Vector3.Normalize(Vector3.Cross(up, direction));
            var crossUp = Vector3.Cross(direction, right);

            return new Matrix4x4(right.X, right.Y, right.Z, -Vector3.Dot(right, eye),
                                 crossUp.X, crossUp.Y, crossUp.Z, -Vector3.Dot(crossUp, eye),
                                 direction.X, direction.Y, direction.Z, -Vector3.Dot(direction, eye),
                                 0f, 0f, 0f, 1f);
        }

        public static Matrix4x4 LookAt_RH(Vector3 eye, Vector3 center, Vector3 up)
        {
            throw new NotImplementedException();
        }

        public LookAtFunction LookAt => mLeftHanded ? LookAt_LH : LookAt_RH;

        #endregion

        private readonly bool mLeftHanded;
        private readonly MinimumDepth mMinDepth;
    }
}