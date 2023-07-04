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
            Rotation = new Vector3(0f);
            Scale = new Vector3(1f);
        }

        /// <summary>
        /// The position of the object
        /// </summary>
        public Vector3 Translation;

        /// <summary>
        /// Rotation, in degrees
        /// </summary>
        public Vector3 Rotation;

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
            ImGui.DragFloat3("Rotation", ref Rotation, 1f);
            ImGui.DragFloat3("Scale", ref Scale, 0.1f);

            return true;
        }

        public static implicit operator Matrix4x4(TransformComponent transform)
        {
            var rotation = transform.Rotation * MathF.PI / 180f;
            var rotationMatrix = Matrix4x4.CreateRotationZ(rotation.Z) *
                                 Matrix4x4.CreateRotationY(rotation.Y) *
                                 Matrix4x4.CreateRotationX(rotation.X); // aligned with SceneLayer.ComputeCameraVectors
            
            return Matrix4x4.Transpose(Matrix4x4.CreateTranslation(transform.Translation) *
                                       rotationMatrix *
                                       Matrix4x4.CreateScale(transform.Scale));
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
            Rotation = MatrixMath.EulerAngles(rotation) * 180f / MathF.PI;
            Scale = scale;

            return true;
        }
    }
}