using CodePlayground.Graphics;
using ImGuiNET;
using Ragdoll.Layers;
using System;
using System.Numerics;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Transform")]
    public sealed class TransformComponent
    {
        public TransformComponent()
        {
            Translation = new Vector3(0f);
            Rotation = Quaternion.Identity;
            Scale = new Vector3(1f);
        }

        /// <summary>
        /// The position of the object
        /// </summary>
        public Vector3 Translation;

        /// <summary>
        /// The rotation of the object
        /// </summary>
        public Quaternion Rotation;

        /// <summary>
        /// The scale of the object for the purposes of rendering and physics
        /// </summary>
        public Vector3 Scale;

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            if (eventInfo.Event != ComponentEventID.Edited)
            {
                return false;
            }

            ImGui.DragFloat3("Translation", ref Translation, 0.1f);

            var quat = new Vector4(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
            if (ImGui.DragFloat4("Rotation (Quaternion)", ref quat, 0.05f))
            {
                Rotation = new Quaternion(quat.X, quat.Y, quat.Z, quat.W);
            }

            var euler = MatrixMath.EulerAngles(Rotation) * 180f / MathF.PI;
            if (ImGui.DragFloat3("Rotation (Euler angles)", ref euler, 1f))
            {
                Rotation = MatrixMath.Quaternion(euler * MathF.PI / 180f);
            }

            ImGui.DragFloat3("Scale", ref Scale, 0.1f);
            return true;
        }

        public static implicit operator Matrix4x4(TransformComponent transform)
        {
            return Matrix4x4.Transpose(Matrix4x4.CreateScale(transform.Scale) *
                                       Matrix4x4.CreateFromQuaternion(transform.Rotation) *
                                       Matrix4x4.CreateTranslation(transform.Translation));
        }

        /// <summary>
        /// Decomposes a 4x4 matrix into the current transform structure
        /// </summary>
        /// <param name="matrix">The input matrix</param>
        /// <returns>If the operation succeeded</returns>
        public bool Decompose(Matrix4x4 matrix) => MatrixMath.Decompose(matrix, out Translation, out Rotation, out Scale);
    }
}