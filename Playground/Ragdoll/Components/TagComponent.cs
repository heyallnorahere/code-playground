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

        public void Edit()
        {
            ImGui.InputText("Tag", ref Tag, 256);
        }
    }
}