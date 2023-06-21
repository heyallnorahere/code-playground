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

        public Vector3 Translation;
        /// <summary>
        /// In degrees
        /// </summary>
        public Vector3 Rotation;
        public Vector3 Scale;

        public void Edit()
        {
            ImGui.DragFloat3("Translation", ref Translation, 0.1f);
            ImGui.DragFloat3("Rotation", ref Rotation, 1f);
            ImGui.DragFloat3("Scale", ref Scale, 0.1f);
        }

        public static implicit operator Matrix4x4(TransformComponent transform)
        {
            var rotation = transform.Rotation * MathF.PI / 180f;
            var rotationMatrix = Matrix4x4.CreateRotationX(rotation.X) *
                                 Matrix4x4.CreateRotationY(rotation.Y) *
                                 Matrix4x4.CreateRotationZ(rotation.Z);
            
            return Matrix4x4.CreateTranslation(transform.Translation) *
                   rotationMatrix *
                   Matrix4x4.CreateScale(transform.Scale);
        }
    }
}