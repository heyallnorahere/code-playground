using CodePlayground.Graphics;
using CodePlayground.Graphics.Animation;
using ImGuiNET;
using Ragdoll.Layers;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Rendered model")]
    public sealed class RenderedModelComponent
    {
        public RenderedModelComponent()
        {
            ID = -1;
            BoneOffset = 0;
            BoneController = null;
        }

        public int ID;
        public int BoneOffset;
        public SkeletonController? BoneController;

        public Model? Model => ID < 0 ? null : App.Instance.ModelRegistry?.Models?[ID].Model;

        private void UpdateBoneTransformArray()
        {
            if (ID < 0)
            {
                BoneController = null;
                return;
            }

            var skeleton = Model?.Skeleton;
            if (skeleton is null)
            {
                BoneController = null;
                return;
            }

            BoneController = new SkeletonController(skeleton);
        }

        private void UpdateBoneOffset(int previous, ulong id)
        {
            var registry = App.Instance.ModelRegistry!;
            if (previous >= 0)
            {
                registry.Models[previous].BoneOffsets.Remove(id);
            }

            if (ID >= 0)
            {
                BoneOffset = registry.CreateBoneOffset(ID, id);
            }
            else
            {
                BoneOffset = 0;
            }
        }

        public void UpdateModel(int modelId, ulong entityId)
        {
            int previousId = ID;
            ID = modelId;

            UpdateBoneTransformArray();
            UpdateBoneOffset(previousId, entityId);
        }

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            var dispatcher = new ComponentEventDispatcher(eventInfo);

            dispatcher.Dispatch(ComponentEventID.Removed, (scene, entity) => UpdateModel(-1, entity));
            dispatcher.Dispatch(ComponentEventID.Edited, OnEdit);

            return dispatcher;
        }

        private unsafe void OnEdit(Scene scene, ulong id)
        {
            var registry = App.Instance.ModelRegistry;
            if (registry is null)
            {
                ImGui.Text("The model registry has not been initialized!");
                return;
            }

            var style = ImGui.GetStyle();
            var font = ImGui.GetFont();
            var regionAvailable = ImGui.GetContentRegionAvail();

            string name = registry.GetFormattedName(ID);
            float lineHeight = font.FontSize + style.FramePadding.Y * 2f;
            float xOffset = regionAvailable.X - lineHeight / 2f;

            ImGui.PushID("model-id");
            ImGui.InputText("Rendered model", ref name, 512, ImGuiInputTextFlags.ReadOnly);

            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload(ModelRegistry.RegisteredModelID);
                if (payload.NativePtr != null)
                {
                    int modelId = Marshal.PtrToStructure<int>(payload.Data);
                    UpdateModel(modelId, id);
                }

                ImGui.EndDragDropTarget();
            }

            bool disabled = ID < 0;
            if (disabled)
            {
                ImGui.BeginDisabled();
            }

            ImGui.SameLine(xOffset);
            if (ImGui.Button("X", Vector2.One * lineHeight))
            {
                UpdateModel(-1, id);
            }

            if (disabled)
            {
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }
    }
}
