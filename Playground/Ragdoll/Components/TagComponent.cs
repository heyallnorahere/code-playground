using ImGuiNET;
using Ragdoll.Layers;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Tag")]
    public sealed class TagComponent
    {
        public TagComponent(string tag = "Entity")
        {
            mTag = tag;
        }

        public string Tag
        {
            get => mTag;
            set => mTag = value;
        }

        public void Edit()
        {
            ImGui.InputText("Tag", ref mTag, 256);
        }

        private string mTag;
    }
}