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

        internal void OnEdit(ulong id, SceneLayer scene)
        {
            ImGui.InputText("Tag", ref Tag, 256);
        }
    }
}