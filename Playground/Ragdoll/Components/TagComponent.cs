using ImGuiNET;
using Ragdoll.Layers;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Tag")]
    public sealed class TagComponent
    {
        public TagComponent(string tag = "Entity")
        {
            Tag = tag;
        }

        public string Tag;

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            if (eventInfo.Event != ComponentEventID.Edited)
            {
                return false;
            }

            ImGui.InputText("Tag", ref Tag, 256);
            return true;
        }
    }
}