using CodePlayground.Graphics;
using ImGuiNET;
using Ragdoll.Layers;
using System;
using System.Numerics;

namespace Ragdoll.Components
{
    [Flags]
    public enum TransformComponents
    {
        Translation = 0x1,
        Rotation = 0x2,
        Scale = 0x4,

        NonDeformative = Translation | Rotation,
        All = Translation | Rotation | Scale
    }

    [RegisteredComponent(DisplayName = "Transform")]
    public sealed class TransformComponent
    {
        public TransformComponent()
        {
            Translation = new Vector3(0f);
            RotationQuat = Quaternion.Identity;
            Scale = new Vector3(1f);
        }

        private Quaternion mQuat;
        private Vector3 mEuler;

        /// <summary>
        /// The position of the object
        /// </summary>
        public Vector3 Translation;

        /// <summary>
        /// The rotation of the object as a quaternion
        /// </summary>
        public Quaternion RotationQuat
        {
            get => mQuat;
            set
            {
                mQuat = value;
                mEuler = MatrixMath.EulerAngles(mQuat);
            }
        }

        /// <summary>
        /// The rotation of the object in radian Euler angles
        /// </summary>
        public Vector3 RotationEuler
        {
            get => mEuler;
            set
            {
                mEuler = value;
                mQuat = MatrixMath.Quaternion(mEuler);
            }
        }

        /// <summary>
        /// The scale of the object for the purposes of rendering and physics
        /// </summary>
        public Vector3 Scale;

        public Matrix4x4 CreateMatrix(TransformComponents components = TransformComponents.All)
        {
            var result = Matrix4x4.Identity;
            if (components.HasFlag(TransformComponents.Translation))
            {
                result = MatrixMath.Translate(Matrix4x4.Identity, Translation);
            }

            if (components.HasFlag(TransformComponents.Rotation))
            {
                result *= MatrixMath.Rotate(mQuat);
            }

            if (components.HasFlag(TransformComponents.Scale))
            {
                result *= MatrixMath.Scale(Matrix4x4.Identity, Scale);
            }

            return result;
        }

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            if (eventInfo.Event != ComponentEventID.Edited)
            {
                return false;
            }

            ImGui.DragFloat3("Translation", ref Translation, 0.1f);

            var quat = new Vector4(mQuat.X, mQuat.Y, mQuat.Z, mQuat.W);
            if (ImGui.DragFloat4("Rotation (Quaternion)", ref quat, 0.05f))
            {
                RotationQuat = new Quaternion(quat.X, quat.Y, quat.Z, quat.W);
            }

            var degrees = mEuler * 180f / MathF.PI;
            if (ImGui.DragFloat3("Rotation (Euler angles)", ref degrees, 1f))
            {
                RotationEuler = degrees * MathF.PI / 180f;
            }

            ImGui.DragFloat3("Scale", ref Scale, 0.1f);
            return true;
        }

        /// <summary>
        /// Decomposes a 4x4 matrix into the current transform structure
        /// </summary>
        /// <param name="matrix">The input matrix</param>
        /// <returns>If the operation succeeded</returns>
        public bool Decompose(Matrix4x4 matrix)
        {
            if (!MatrixMath.Decompose(matrix, out Vector3 translation, out Quaternion rotation, out Vector3 scale))
            {
                return false;
            }

            Translation = translation;
            RotationQuat = rotation;
            Scale = scale;

            return true;
        }
    }
}