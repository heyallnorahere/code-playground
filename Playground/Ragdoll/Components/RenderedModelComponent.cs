using ImGuiNET;
using Ragdoll.Layers;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Rendered model")]
    public sealed class RenderedModelComponent
    {
        public const string ModelDragDropID = "registered-model";

        public RenderedModelComponent()
        {
            ID = -1;
        }

        public int ID;

        internal void OnEdit(ulong id, SceneLayer scene)
        {
            ImGui.
        }
    }
}
