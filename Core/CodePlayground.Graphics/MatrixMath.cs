using Optick.NET;
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

        // https://github.com/StudioCherno/Hazel/blob/master/Hazel/src/Hazel/Math/Math.cpp#L15
        public static bool Decompose(Matrix4x4 matrix, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            translation = matrix.Translation;
            rotation = new Quaternion(0f, 0f, 0f, 1f);
            scale = new Vector3(1f);

            for (int i = 0; i < 4; i++)
            {
                if (MathF.Abs(matrix[3, i]) < float.Epsilon)
                {
                    return false;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                float length2 = 0f;
                for (int j = 0; j < 3; j++)
                {
                    float value = matrix[j, i];
                    length2 += MathF.Pow(value, 2f);
                }

                scale[i] = MathF.Sqrt(length2);
                for (int j = 0; j < 3; j++)
                {
                    matrix[j, i] /= scale[i];
                }
            }

            float trace = 0f;
            for (int i = 0; i < 3; i++)
            {
                trace += matrix[i, 0];
            }

            if (trace > 0f)
            {
                float root = MathF.Sqrt(trace + 1f);
                rotation.W = root / 2f;

                for (int i = 0; i < 3; i++)
                {
                    int j = (i + 1) % 3;
                    int k = (j + 1) % 3;

                    rotation[i] = (matrix[k, j] - matrix[j, k]) / (root * 2f);
                }
            }
            else
            {
                int i = -1;
                for (int l = 0; l < 3; l++)
                {
                    if (i < 0 || matrix[l, l] > matrix[i, i])
                    {
                        i = l;
                    }
                }

                int j = (i + 1) % 3;
                int k = (j + 1) % 3;

                float root = MathF.Sqrt(1f + matrix[i, i] - matrix[j, j] - matrix[k, k]);
                rotation[i] = root / 2f;

                float f = 1f / (root * 2f);
                rotation[j] = f * (matrix[j, i] + matrix[i, j]);
                rotation[k] = f * (matrix[k, i] + matrix[i, k]);
                rotation.W = f * (matrix[k, j] - matrix[j, k]);
            }

            return true;
        }

        public static Vector3 EulerAngles(Quaternion quat)
        {
            using var conversionEvent = OptickMacros.Event();
            return new Vector3
            {
                X = Pitch(quat),
                Y = Yaw(quat),
                Z = Roll(quat)
            };
        }

        // https://github.com/g-truc/glm/blob/master/glm/detail/type_quat.inl#L208
        public static Quaternion Quaternion(Vector3 eulerAngles)
        {
            using var conversionEvent = OptickMacros.Event();

            Vector3 s, c;
            s = c = Vector3.Zero;

            for (int i = 0; i < 3; i++)
            {
                float angle = eulerAngles[i] / 2f;
                s[i] = MathF.Sin(angle);
                c[i] = MathF.Cos(angle);
            }

            var result = new Quaternion();
            for (int i = 0; i < 4; i++)
            {
                float sine, cosine;
                sine = cosine = 1f;

                for (int j = 0; j < 3; j++)
                {
                    sine *= (i != j) ? s[j] : c[j];
                    cosine *= (i != j) ? c[j] : s[j];
                }

                result[i] = sine + cosine;
            }

            return result;
        }


        // https://github.com/g-truc/glm/blob/5c46b9c07008ae65cb81ab79cd677ecc1934b903/glm/gtc/quaternion.inl#L28
        public static float Pitch(Quaternion quat)
        {
            using var angleEvent = OptickMacros.Event();

            float opposite = 2f * (quat.Y * quat.Z + quat.W * quat.X);
            float adjacent = MathF.Pow(quat.W, 2f) + MathF.Pow(quat.Z, 2f) - MathF.Pow(quat.X, 2f) - MathF.Pow(quat.Y, 2f);

            if (MathF.Abs(opposite) < float.Epsilon && MathF.Abs(adjacent) < float.Epsilon)
            {
                return 2f * MathF.Atan2(quat.X, quat.W);
            }

            return MathF.Atan2(opposite, adjacent);
        }

        public static float Yaw(Quaternion quat)
        {
            using var angleEvent = OptickMacros.Event();

            float sin = float.Clamp(-2f * (quat.X * quat.Z - quat.W * quat.Y), -1f, 1f);
            return MathF.Asin(sin);
        }

        public static float Roll(Quaternion quat)
        {
            using var angleEvent = OptickMacros.Event();

            float opposite = 2f * (quat.X * quat.Y + quat.W * quat.Z);
		    float adjacent = MathF.Pow(quat.W, 2f) + MathF.Pow(quat.X, 2f) - MathF.Pow(quat.Y, 2f) - MathF.Pow(quat.Z, 2f);

            if (MathF.Abs(opposite) < float.Epsilon && MathF.Abs(adjacent) < float.Epsilon)
            {
                return 0f;
            }

            return MathF.Atan2(opposite, adjacent);
        }

        #region Perspective

        // https://github.com/g-truc/glm/blob/efec5db081e3aad807d0731e172ac597f6a39447/glm/ext/matrix_clip_space.inl#L265
        public static Matrix4x4 Perspective_LH_Z0(float fov, float aspectRatio, float nearPlane, float farPlane)
        {
            using var projectionMatrixEvent = OptickMacros.Event();

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

        // https://github.com/g-truc/glm/blob/efec5db081e3aad807d0731e172ac597f6a39447/glm/ext/matrix_clip_space.inl#L16
        public static Matrix4x4 Orthographic_LH_Z0(float left, float right, float bottom, float top, float nearPlane, float farPlane)
        {
            using var projectionMatrixEvent = OptickMacros.Event();

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
            using var lookAtEvent = OptickMacros.Event();

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