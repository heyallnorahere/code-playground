using CodePlayground;
using ImGuiNET;
using System;
using System.Runtime.InteropServices;

namespace Ragdoll
{
    public static class ImGuiUtilities
    {
        public static bool DragDropEntityTarget(string id, Scene scene, ref ulong entity, Func<ulong, bool>? selector = null)
        {
            using var targetEvent = Profiler.Event();

            string value = entity != Scene.Null ? scene.GetDisplayedEntityTag(entity) : "--No entity--";
            ImGui.InputText(id, ref value, (uint)value.Length, ImGuiInputTextFlags.ReadOnly);

            if (DragDropTarget(Scene.EntityDragDropID, out ulong newEntity, selector))
            {
                entity = newEntity;
                return true;
            }

            return false;
        }

        public static unsafe bool DragDropTarget<T>(string type, out T result, Func<T, bool>? selector = null) where T : unmanaged
        {
            using var targetEvent = Profiler.Event();

            result = default;
            if (!ImGui.BeginDragDropTarget())
            {
                return false;
            }

            bool succeeded = false;
            if (selector is not null)
            {
                var payload = ImGui.AcceptDragDropPayload(type, ImGuiDragDropFlags.AcceptPeekOnly);
                if (payload.NativePtr != null)
                {
                    var value = Marshal.PtrToStructure<T>(payload.Data);
                    if (selector.Invoke(value))
                    {
                        payload = ImGui.AcceptDragDropPayload(type);
                        if (payload.NativePtr != null)
                        {
                            result = value;
                            succeeded = true;
                        }
                    }
                }
            }
            else
            {
                var payload = ImGui.AcceptDragDropPayload(type);
                if (payload.NativePtr != null)
                {
                    result = Marshal.PtrToStructure<T>(payload.Data);
                    succeeded = true;
                }
            }

            ImGui.EndDragDropTarget();
            return succeeded;
        }
    }
}