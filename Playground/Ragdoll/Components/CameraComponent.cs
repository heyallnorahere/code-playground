using ImGuiNET;
using Ragdoll.Layers;
using System.Numerics;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Camera")]
    public sealed class CameraComponent
    {
        public CameraComponent()
        {
            FOV = 45f;
            RotationOffset = Vector3.Zero;
            MainCamera = false;
        }

        public float FOV;
        public Vector3 RotationOffset;
        public bool MainCamera;

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            if (eventInfo.Event != ComponentEventID.Edited)
            {
                return false;
            }

            ImGui.SliderFloat("Vertical FOV", ref FOV, 1f, 89f);
            ImGui.DragFloat3("Rotation offset", ref RotationOffset, 1f);
            ImGui.Checkbox("Main camera", ref MainCamera);

            return true;
        }
    }
}