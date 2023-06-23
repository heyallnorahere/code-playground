using CodePlayground.Graphics;
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
        public const string ModelDragDropID = "registered-model";

        public RenderedModelComponent()
        {
            ID = -1;
            BoneOffset = 0;
            BoneTransforms = null;
        }

        public int ID;
        public int BoneOffset;
        public Matrix4x4[]? BoneTransforms;

        public Model? Model => ID < 0 ? null : App.Instance.ModelRegistry?.Models?[ID].Model;

        private void UpdateBoneTransformArray()
        {
            if (ID < 0)
            {
                BoneTransforms = null;
                return;
            }

            var skeleton = Model?.Skeleton;
            if (skeleton is null)
            {
                BoneTransforms = null;
                return;
            }

            BoneTransforms = new Matrix4x4[skeleton.BoneCount];
            Array.Fill(BoneTransforms, Matrix4x4.Identity);
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

        internal unsafe void OnEdit(ulong id, SceneLayer scene)
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
                var payload = ImGui.AcceptDragDropPayload(ModelDragDropID);
                if (payload.NativePtr != null)
                {
                    int previousId = ID;
                    ID = Marshal.PtrToStructure<int>(payload.Data);

                    UpdateBoneTransformArray();
                    UpdateBoneOffset(previousId, id);
                }

                ImGui.EndDragDropTarget();
            }

            bool disabled = ID < 0;
            if (disabled)
            {
                ImGui.BeginDisabled();
            }

            ImGui.SameLine(xOffset);
            if (ImGui.Button("X", new Vector2(lineHeight)))
            {
                int previousId = ID;
                ID = -1;

                UpdateBoneTransformArray();
                UpdateBoneOffset(previousId, id);
            }

            if (disabled)
            {
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }

        internal void OnComponentRemoved(ulong id, SceneLayer scene)
        {
            int previousId = ID;
            ID = -1;

            UpdateBoneOffset(previousId, id);
        }
    }
}
