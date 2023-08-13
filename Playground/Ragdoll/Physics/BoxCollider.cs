using BepuPhysics.Collidables;
using ImGuiNET;
using Optick.NET;
using Ragdoll.Components;
using System.Numerics;

namespace Ragdoll.Physics
{
    [RegisteredCollider(ColliderType.Box)]
    public sealed class BoxCollider : Collider
    {
        public override void Initialize(Scene scene, ulong id)
        {
            mSize = Vector3.One;
            mScene = scene;
            mEntity = id;

            Invalidate(false);
        }

        public override void Edit()
        {
            bool invalidate = false;

            const float speed = 0.05f;
            invalidate |= ImGui.DragFloat("Width", ref mSize.X, speed);
            invalidate |= ImGui.DragFloat("Height", ref mSize.Y, speed);
            invalidate |= ImGui.DragFloat("Length", ref mSize.Z, speed);

            if (invalidate)
            {
                Invalidate();
            }
        }

        public override void Cleanup()
        {
            var simulation = mScene!.Simulation;
            simulation.Shapes.RemoveAndDispose(mIndex, simulation.BufferPool);
        }

        public override void Update()
        {
            if (!mScene!.TryGetComponent(mEntity, out TransformComponent? transform))
            {
                return;
            }

            if ((mCurrentScale - transform.Scale).Length() > float.Epsilon)
            {
                Invalidate();
            }
        }

        public override void Invalidate() => Invalidate(true);
        private void Invalidate(bool removeOld)
        {
            using var invalidateEvent = OptickMacros.Event();

            var oldIndex = mIndex;
            var simulation = mScene!.Simulation;

            var size = mSize;
            if (mScene!.TryGetComponent(mEntity, out TransformComponent? transform))
            {
                size *= mCurrentScale = transform.Scale;
            }

            var shape = new Box(size.X, size.Y, size.Z);
            mIndex = simulation.Shapes.Add(shape);
            TriggerOnChanged(mIndex, shape.ComputeInertia);

            if (removeOld)
            {
                simulation.Shapes.RemoveAndDispose(oldIndex, simulation.BufferPool);
            }
        }

        public Vector3 Size
        {
            get => mSize;
            set
            {
                mSize = value;
                Invalidate();
            }
        }

        private Vector3 mSize;
        private Scene? mScene;
        private ulong mEntity;

        private TypedIndex mIndex;
        private Vector3 mCurrentScale;
    }
}