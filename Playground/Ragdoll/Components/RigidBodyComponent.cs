using BepuPhysics;
using BepuPhysics.Collidables;
using CodePlayground;
using CodePlayground.Graphics;
using ImGuiNET;
using Optick.NET;
using Ragdoll.Layers;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ragdoll.Components
{
    public enum ColliderType
    {
        Box,
        StaticModel
    }

    public enum BodyType
    {
        Dynamic,
        Kinematic,
        Static
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class RegisteredColliderAttribute : Attribute
    {
        public RegisteredColliderAttribute(ColliderType type)
        {
            Type = type;
        }

        public ColliderType Type { get; }
    }

    public abstract class Collider
    {
        public Collider()
        {
            var type = GetType();
            var attribute = type.GetCustomAttribute<RegisteredColliderAttribute>();

            if (attribute is null)
            {
                throw new InvalidOperationException("This collider type is not registered!");
            }

            Type = attribute.Type;
        }

        public ColliderType Type { get; }

        public event Action<TypedIndex, BodyInertia>? OnChanged;

        protected void TriggerOnChanged(TypedIndex index, BodyInertia inertia)
        {
            OnChanged?.Invoke(index, inertia);
        }

        public abstract void Initialize(Scene scene, ulong id);
        public abstract void Edit();
        public abstract void Cleanup();
        public abstract void Update();
    }

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

        private void Invalidate(bool removeOld = true)
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
            TriggerOnChanged(mIndex, shape.ComputeInertia(1f));

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

    [RegisteredCollider(ColliderType.StaticModel)]
    public sealed class StaticModelCollider : Collider
    {
        public override void Initialize(Scene scene, ulong id)
        {
            mScene = scene;
            mEntity = id;

            mScene.TryGetComponent(mEntity, out TransformComponent? transform);
            mCurrentScale = transform?.Scale ?? Vector3.One;

            mModel = -1;
            Invalidate(force: true);
        }

        public override void Cleanup()
        {
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
            mScene!.TryGetComponent(mEntity, out TransformComponent? transform);
            Invalidate(transform?.Scale);
        }

        public unsafe override void Edit()
        {
            var registry = App.Instance.ModelRegistry;

            var style = ImGui.GetStyle();
            var font = ImGui.GetFont();
            var regionAvailable = ImGui.GetContentRegionAvail();

            string name = registry!.GetFormattedName(mModel);
            float lineHeight = font.FontSize + style.FramePadding.Y * 2f;
            float xOffset = regionAvailable.X - lineHeight / 2f;

            ImGui.PushID("collision-mesh-id");
            ImGui.InputText("Collision mesh", ref name, 512, ImGuiInputTextFlags.ReadOnly);

            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload(ModelRegistry.RegisteredModelID, ImGuiDragDropFlags.AcceptPeekOnly);
                if (payload.NativePtr != null)
                {
                    int modelId = Marshal.PtrToStructure<int>(payload.Data);
                    var model = registry.Models[modelId];

                    if (model.Model.Skeleton is null)
                    {
                        payload = ImGui.AcceptDragDropPayload(ModelRegistry.RegisteredModelID);
                        if (payload.NativePtr != null)
                        {
                            int oldModel = mModel;
                            mModel = modelId;

                            Invalidate(oldModel: oldModel);
                        }
                    }
                }

                ImGui.EndDragDropTarget();
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

        private void Invalidate(Vector3? scale = null, int oldModel = -1, bool force = false)
        {
            using var invalidateEvent = OptickMacros.Event();

            var registry = App.Instance.ModelRegistry;
            if (registry is null)
            {
                throw new InvalidOperationException();
            }

            bool modelsDiffer = oldModel != mModel;
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
                TriggerOnChanged(physicsData.Shape, physicsData.Inertia);
            }
            else
            {
                var placeholder = new Capsule((mCurrentScale.X + mCurrentScale.Z) / 4f, mCurrentScale.Y);
                var placeholderIndex = simulation.Shapes.Add(placeholder);

                mPlaceholderShape = placeholderIndex;
                TriggerOnChanged(placeholderIndex, placeholder.ComputeInertia(1f));
            }
        }

        private Scene? mScene;
        private ulong mEntity;
        private Vector3 mCurrentScale;

        private int mModel;
        private TypedIndex? mPlaceholderShape;
    }

    [RegisteredComponent(DisplayName = "Rigid body")]
    public sealed class RigidBodyComponent
    {
        private static readonly Dictionary<ColliderType, ConstructorInfo> sRegisteredColliders;
        static RigidBodyComponent()
        {
            sRegisteredColliders = new Dictionary<ColliderType, ConstructorInfo>();

            var assembly = Assembly.GetExecutingAssembly();
            var types = assembly.GetTypes();

            foreach (var type in types)
            {
                if (!type.Extends<Collider>())
                {
                    continue;
                }

                var attribute = type.GetCustomAttribute<RegisteredColliderAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Array.Empty<Type>());
                if (constructor is null)
                {
                    throw new ArgumentException($"No suitable constructor found for type {type}!");
                }

                sRegisteredColliders.Add(attribute.Type, constructor);
            }
        }

        public RigidBodyComponent(ColliderType collider = ColliderType.Box)
        {
            mColliderType = collider;
            mCollider = null;
            mColliderInitialized = false;
            mShapeIndex = new TypedIndex(-1, -1);

            BodyType = BodyType.Dynamic;
            Mass = 1f;
            SleepThreshold = 0.01f;
        }

        private void CleanupCollider()
        {
            if (mCollider is null)
            {
                return;
            }

            mCollider.OnChanged -= OnColliderChanged;
            mCollider.Cleanup();
        }

        [MemberNotNull(nameof(mCollider))]
        public void CreateCollider(ColliderType collider)
        {
            using var createEvent = OptickMacros.Event();

            if (mScene is null)
            {
                throw new InvalidOperationException("Cannot initialize a collider before the component is attached!");
            }

            var newCollider = (Collider)sRegisteredColliders[collider].Invoke(null);
            CleanupCollider();

            mCollider = newCollider;
            mCollider.OnChanged += OnColliderChanged;
            mCollider.Initialize(mScene, mEntity);
        }

        private void OnColliderChanged(TypedIndex index, BodyInertia inertia)
        {
            using var changedEvent = OptickMacros.Event();

            mShapeIndex = index;
            mInertia = inertia;

            var simulation = mScene!.Simulation;
            var body = simulation.Bodies[mHandle];

            if (mColliderInitialized)
            {
                body.SetShape(mShapeIndex);
                body.SetLocalInertia(ComputeInertia(Mass));
            }
        }

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            var dispatcher = new ComponentEventDispatcher(eventInfo);

            dispatcher.Dispatch(ComponentEventID.Added, OnComponentAdded);
            dispatcher.Dispatch(ComponentEventID.Removed, OnComponentRemoved);
            dispatcher.Dispatch(ComponentEventID.Edited, OnEdit);
            dispatcher.Dispatch(ComponentEventID.PrePhysicsUpdate, PrePhysicsUpdate);
            dispatcher.Dispatch(ComponentEventID.PostPhysicsUpdate, PostPhysicsUpdate);

            return dispatcher;
        }

        private BodyInertia ComputeInertia(float mass)
        {
            return new BodyInertia
            {
                InverseMass = 1f / mass,
                InverseInertiaTensor = mInertia.InverseInertiaTensor * (1f / (mass * mInertia.InverseMass))
            };
        }

        private void OnComponentAdded(Scene scene, ulong id)
        {
            using var addedEvent = OptickMacros.Event();
            var simulation = scene.Simulation;

            mScene = scene;
            mEntity = id;

            CreateCollider(mColliderType);
            mColliderInitialized = true;

            mScene.TryGetComponent(mEntity, out TransformComponent? transform);
            var rigidPose = new RigidPose
            {
                Position = transform?.Translation ?? Vector3.Zero,
                Orientation = transform?.CalculateQuaternion() ?? Quaternion.Zero
            };

            var body = BodyDescription.CreateDynamic(rigidPose, ComputeInertia(Mass), mShapeIndex, SleepThreshold);
            mHandle = simulation.Bodies.Add(body);
        }

        private void OnComponentRemoved()
        {
            using var removedEvent = OptickMacros.Event();

            mColliderInitialized = false;
            CleanupCollider();

            mScene!.Simulation.Bodies.Remove(mHandle);
        }

        private void OnEdit()
        {
            using var editedEvent = OptickMacros.Event();

            var simulation = mScene!.Simulation;
            var body = simulation.Bodies[mHandle];

            var bodyTypes = Enum.GetValues<BodyType>().Select(type => type.ToString()).ToArray();
            int item = (int)BodyType;

            bool updateMass = false;
            if (ImGui.Combo("Body type", ref item, bodyTypes, bodyTypes.Length))
            {
                BodyType = (BodyType)item;
                switch (BodyType)
                {
                    case BodyType.Dynamic:
                        updateMass = true;
                        break;
                    case BodyType.Kinematic:
                        body.BecomeKinematic();
                        break;
                    default:
                        // TODO: implement static bodies
                        throw new NotImplementedException();
                }
            }

            float threshold = SleepThreshold;
            if (ImGui.InputFloat("Sleep threshold", ref threshold))
            {
                body.Activity.SleepThreshold = SleepThreshold = threshold;
            }

            float mass = Mass;
            if (ImGui.DragFloat("Mass", ref mass, 0.05f))
            {
                Mass = mass;

                if (BodyType != BodyType.Kinematic)
                {
                    updateMass = true;
                }
            }

            if (updateMass)
            {
                body.SetLocalInertia(ComputeInertia(mass));
            }

            var colliderType = mCollider!.Type;
            if (ImGui.BeginCombo("Collider type", colliderType.ToString()))
            {
                var colliderTypes = Enum.GetValues<ColliderType>();
                foreach (var currentType in colliderTypes)
                {
                    bool isSelected = colliderType == currentType;
                    bool isDisabled = !sRegisteredColliders.ContainsKey(currentType);

                    if (isDisabled)
                    {
                        ImGui.BeginDisabled();
                    }

                    var name = currentType.ToString();
                    if (ImGui.Selectable(name, isSelected))
                    {
                        CreateCollider(currentType);
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }

                    if (isDisabled)
                    {
                        ImGui.EndDisabled();
                    }
                }

                ImGui.EndCombo();
            }

            mCollider.Edit();
        }

        public Collider Collider => mCollider!;
        public BodyType BodyType { get; set; }
        public float Mass { get; set; }
        public float SleepThreshold { get; set; }

        public BodyHandle Handle => mHandle;

        public void PrePhysicsUpdate()
        {
            using var prePhysicsUpdateEvent = OptickMacros.Event();

            var simulation = mScene!.Simulation;
            var body = simulation.Bodies[mHandle];

            body.Activity.SleepThreshold = SleepThreshold;
            if (mScene.TryGetComponent<TransformComponent>(mEntity, out TransformComponent? transform))
            {
                body.Pose.Position = transform.Translation;
                body.Pose.Orientation = transform.CalculateQuaternion();
            }

            mCollider!.Update();
        }

        public void PostPhysicsUpdate()
        {
            using var postPhysicsUpdateEvent = OptickMacros.Event();
            if (!mScene!.TryGetComponent(mEntity, out TransformComponent? transform))
            {
                return;
            }

            var simulation = mScene.Simulation;
            var body = simulation.Bodies[mHandle];

            transform.Translation = body.Pose.Position;
            transform.Rotation = MatrixMath.EulerAngles(body.Pose.Orientation) * 180f / MathF.PI;
        }

        private ColliderType mColliderType;
        private Collider? mCollider;
        private bool mColliderInitialized;

        private TypedIndex mShapeIndex;
        private BodyInertia mInertia;

        private BodyHandle mHandle;

        private Scene? mScene;
        private ulong mEntity;
    }
}