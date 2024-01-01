using BepuPhysics.Collidables;
using CodePlayground;
using ImGuiNET;
using Ragdoll.Components;
using System;
using System.Numerics;

namespace Ragdoll.Physics
{
    [RegisteredCollider(ColliderType.StaticModel)]
    public sealed class StaticModelCollider : Collider
    {
        public override void Initialize(Scene scene, ulong id)
        {
            using var initializeEvent = Profiler.Event();

            mScene = scene;
            mEntity = id;

            mScene.TryGetComponent(mEntity, out TransformComponent? transform);
            mCurrentScale = transform?.Scale ?? Vector3.One;

            mModel = -1;
            Invalidate(force: true);
        }

        public override void Cleanup()
        {
            using var cleanupEvent = Profiler.Event();

            if (mModel >= 0)
            {
                var registry = App.Instance.ModelRegistry;
                registry!.RemoveEntityCollider(mModel, mScene!, mEntity);
            }
            else if (mPlaceholderShape is not null)
            {
                var simulation = mScene!.Simulation;
                simulation.Shapes.RemoveAndDispose(mPlaceholderShape.Value, simulation.BufferPool);
            }
        }

        public override void Update()
        {
            using var updateEvent = Profiler.Event();

            mScene!.TryGetComponent(mEntity, out TransformComponent? transform);
            Invalidate(transform?.Scale);
        }

        public unsafe override void Edit()
        {
            using var editEvent = Profiler.Event();
            var registry = App.Instance.ModelRegistry;

            var style = ImGui.GetStyle();
            var font = ImGui.GetFont();
            var regionAvailable = ImGui.GetContentRegionAvail();

            string name = registry!.GetFormattedName(mModel);
            float lineHeight = font.FontSize + style.FramePadding.Y * 2f;
            float xOffset = regionAvailable.X - lineHeight / 2f;

            ImGui.PushID("collision-mesh-id");
            ImGui.InputText("Collision mesh", ref name, 512, ImGuiInputTextFlags.ReadOnly);

            if (ImGuiUtilities.DragDropTarget(ModelRegistry.RegisteredModelID, out int modelId, model => registry.Models[model].Model.Skeleton is null))
            {
                int oldModel = mModel;
                mModel = modelId;

                Invalidate(oldModel: oldModel);
            }

            bool disabled = mModel < 0;
            if (disabled)
            {
                ImGui.BeginDisabled();
            }

            ImGui.SameLine(xOffset);
            if (ImGui.Button("X", Vector2.One * lineHeight))
            {
                int oldModel = mModel;
                mModel = -1;

                Invalidate(oldModel: oldModel);
            }

            if (disabled)
            {
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }

        public override void Invalidate() => Invalidate(force: true);
        private void Invalidate(Vector3? scale = null, int oldModel = -2, bool force = false)
        {
            using var invalidateEvent = Profiler.Event();
            var registry = App.Instance.ModelRegistry ?? throw new InvalidOperationException();

            bool modelsDiffer = oldModel != mModel && oldModel >= -1;
            if (!force && !modelsDiffer &&
                !(scale.HasValue && (scale.Value - mCurrentScale).Length() > float.Epsilon))
            {
                return;
            }

            var simulation = mScene!.Simulation;
            if (modelsDiffer && oldModel >= 0)
            {
                registry.RemoveEntityCollider(oldModel, mScene!, mEntity);
            }
            else if (mPlaceholderShape is not null)
            {
                simulation.Shapes.RemoveAndDispose(mPlaceholderShape.Value, simulation.BufferPool);
                mPlaceholderShape = null;
            }

            if (scale.HasValue)
            {
                mCurrentScale = scale.Value;
            }

            if (mModel >= 0)
            {
                registry.SetEntityColliderScale(mModel, mScene, mEntity, mCurrentScale);

                var modelData = registry.Models[mModel];
                var physicsData = modelData.PhysicsData[mEntity];
                TriggerOnChanged(physicsData.Index, physicsData.ComputeInertia);
            }
            else
            {
                var placeholder = new Capsule((mCurrentScale.X + mCurrentScale.Z) / 4f, mCurrentScale.Y);
                var placeholderIndex = simulation.Shapes.Add(placeholder);

                mPlaceholderShape = placeholderIndex;
                TriggerOnChanged(placeholderIndex, placeholder.ComputeInertia);
            }
        }

        public bool SetModel(int modelId)
        {
            using var setModelEvent = Profiler.Event();

            var registry = App.Instance.ModelRegistry;
            var model = registry!.Models[modelId];

            if (model.Model.Skeleton is not null)
            {
                return false;
            }

            int oldModel = mModel;
            mModel = modelId;

            Invalidate(oldModel: oldModel);
            return true;
        }

        private Scene? mScene;
        private ulong mEntity;
        private Vector3 mCurrentScale;

        private int mModel;
        private TypedIndex? mPlaceholderShape;
    }
}