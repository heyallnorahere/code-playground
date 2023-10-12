using ImGuiNET;
using Ragdoll.Layers;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Camera")]
    public sealed class CameraComponent
    {
        public CameraComponent()
        {
            FOV = 45f;
            MainCamera = false;
        }

        public float FOV;
        public bool MainCamera;

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            if (eventInfo.Event != ComponentEventID.Edited)
            {
                return false;
            }

            ImGui.SliderFloat("Vertical FOV", ref FOV, 1f, 89f);
            ImGui.Checkbox("Main camera", ref MainCamera);

            return true;
        }
    }
}