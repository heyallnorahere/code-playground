using BepuPhysics;
using BepuPhysics.Collidables;
using CodePlayground;
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

        public event Action<TypedIndex, Func<float, BodyInertia>>? OnChanged;

        protected void TriggerOnChanged(TypedIndex index, Func<float, BodyInertia> computeInertia)
        {
            OnChanged?.Invoke(index, computeInertia);
        }

        public abstract void Initialize(Scene scene, ulong id);
        public abstract void Edit();
        public abstract void Cleanup();
        public abstract void Update();
        public abstract void Invalidate();
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

        public override void Invalidate() => Invalidate(force: true);
        private void Invalidate(Vector3? scale = null, int oldModel = -1, bool force = false)
        {
            using var invalidateEvent = OptickMacros.Event();

            var registry = App.Instance.ModelRegistry;
            if (registry is null)
            {
                throw new InvalidOperationException();
            }

            bool modelsDiffer = oldModel != mModel && oldModel >= 0;
            if (!force && !modelsDiffer &&
                !(scale.HasValue && (scale.Value - mCurrentScale).Length() > float.Epsilon))
            {
                return;
            }

            var simulation = mScene!.Simulation;
            if (modelsDiffer)
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
            mInitialColliderType = collider;
            mCollider = null;
            mColliderInitialized = false;

            mBodyType = BodyType.Dynamic;
            mMass = 1f;
            mSleepThreshold = 0.01f;
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
        public Collider CreateCollider(ColliderType collider)
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

            return mCollider;
        }

        private void OnColliderChanged(TypedIndex index, Func<float, BodyInertia> computeInertia)
        {
            using var changedEvent = OptickMacros.Event();

            mShapeIndex = index;
            mComputeInertia = computeInertia;

            var simulation = mScene!.Simulation;
            if (mColliderInitialized)
            {
                if (mBodyType != BodyType.Static)
                {
                    var body = simulation.Bodies[mBody];
                    body.SetShape(mShapeIndex);
                    body.SetLocalInertia(mComputeInertia.Invoke(Mass));

                    body.UpdateBounds();
                }
                else
                {
                    var staticRef = simulation.Statics[mStatic];
                    staticRef.SetShape(mShapeIndex);

                    staticRef.UpdateBounds();
                }
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

        private BodyInertia ComputeInertia(float mass) => mComputeInertia!.Invoke(mass);
        private void UpdateMass()
        {
            if (mBodyType != BodyType.Dynamic)
            {
                return;
            }

            var inertia = ComputeInertia(mMass);
            mScene!.Simulation.Bodies[mBody].SetLocalInertia(inertia);
        }

        private RigidPose GetRigidPose()
        {
            mScene!.TryGetComponent(mEntity, out TransformComponent? transform);
            return new RigidPose
            {
                Position = transform?.Translation ?? Vector3.Zero,
                Orientation = transform?.RotationQuat ?? Quaternion.Zero
            };
        }

        private void OnComponentAdded(Scene scene, ulong id)
        {
            using var addedEvent = OptickMacros.Event();

            mScene = scene;
            mEntity = id;

            CreateCollider(mInitialColliderType);
            mColliderInitialized = true;

            CreateBody();
        }

        private void CreateBody()
        {
            var collidable = new CollidableDescription(mShapeIndex, ContinuousDetection.Passive);
            var body = BodyDescription.CreateDynamic(GetRigidPose(), ComputeInertia(Mass), collidable, mSleepThreshold);

            var simulation = mScene!.Simulation;
            mBody = simulation.Bodies.Add(body);
        }

        private void CreateStatic()
        {
            var staticDesc = new StaticDescription(GetRigidPose(), mShapeIndex, ContinuousDetection.Passive);

            var simulation = mScene!.Simulation;
            mStatic = simulation.Statics.Add(staticDesc);
        }

        private void DestroyBody(BodyType previousBodyType)
        {
            var simulation = mScene!.Simulation;
            if (previousBodyType != BodyType.Static)
            {
                simulation.Bodies.Remove(mBody);
            }
            else
            {
                simulation.Statics.Remove(mStatic);
            }
        }

        private void Invalidate(BodyType previousBodyType)
        {
            var isBody = mBodyType != BodyType.Static;
            if (isBody && previousBodyType != BodyType.Static)
            {
                return;
            }

            using var invalidateEvent = OptickMacros.Event();
            DestroyBody(previousBodyType);

            if (isBody)
            {
                CreateBody();
            }
            else
            {
                CreateStatic();
            }
        }

        private void OnComponentRemoved()
        {
            using var removedEvent = OptickMacros.Event();

            mColliderInitialized = false;
            CleanupCollider();

            mScene!.Simulation.Bodies.Remove(mBody);
        }

        private void OnEdit()
        {
            using var editedEvent = OptickMacros.Event();
            var simulation = mScene!.Simulation;

            var bodyTypes = Enum.GetValues<BodyType>().Select(type => type.ToString()).ToArray();
            int item = (int)mBodyType;

            if (ImGui.Combo("Body type", ref item, bodyTypes, bodyTypes.Length))
            {
                var previous = mBodyType;
                mBodyType = (BodyType)item;

                TransitionBodyType(previous);
            }

            bool isBody = mBodyType != BodyType.Static;
            if (!isBody)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.InputFloat("Sleep threshold", ref mSleepThreshold))
            {
                simulation.Bodies[mBody].Activity.SleepThreshold = mSleepThreshold;
            }

            bool awake = !isBody || simulation.Bodies[mBody].Awake;
            if (ImGui.Checkbox("Awake", ref awake))
            {
                var body = simulation.Bodies[mBody];
                body.Awake = awake;
            }

            if (isBody && mBodyType != BodyType.Dynamic)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.DragFloat("Mass", ref mMass, 0.05f))
            {
                UpdateMass();
            }

            if (mBodyType != BodyType.Dynamic)
            {
                ImGui.EndDisabled();
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
            if (isBody)
            {
                ImGui.PushID("velocity");
                if (ImGui.CollapsingHeader("Velocity"))
                {
                    ImGui.Indent();

                    ref BodyVelocity velocity = ref simulation.Bodies[mBody].Velocity;
                    if (ImGui.Button("Reset"))
                    {
                        velocity.Linear = Vector3.Zero;
                        velocity.Angular = Vector3.Zero;
                    }

                    ImGui.InputFloat3("Linear", ref velocity.Linear, "%.3f", ImGuiInputTextFlags.ReadOnly);
                    ImGui.SameLine();

                    if (ImGui.Button("Reset linear"))
                    {
                        velocity.Linear = Vector3.Zero;
                    }

                    var angular = velocity.Angular * 180f / MathF.PI;
                    ImGui.InputFloat3("Angular", ref angular, "%.3f", ImGuiInputTextFlags.ReadOnly);
                    ImGui.SameLine();

                    if (ImGui.Button("Reset angular"))
                    {
                        velocity.Angular = Vector3.Zero;
                    }

                    ImGui.Unindent();
                }

                ImGui.PopID();
            }
        }

        private void TransitionBodyType(BodyType previous)
        {
            Invalidate(previous);

            var simulation = mScene!.Simulation;
            switch (BodyType)
            {
                case BodyType.Dynamic:
                    UpdateMass();
                    break;
                case BodyType.Kinematic:
                    simulation.Bodies[mBody].BecomeKinematic();
                    break;
            }
        }

        public BodyType BodyType
        {
            get => mBodyType;
            set
            {
                var previous = mBodyType;
                mBodyType = value;

                TransitionBodyType(previous);
            }
        }

        public Collider Collider => mCollider!;
        public float Mass
        {
            get => mMass;
            set
            {
                mMass = value;
                UpdateMass();
            }
        }

        public float SleepThreshold
        {
            get => mSleepThreshold;
            set
            {
                mSleepThreshold = value;
                if (mBodyType != BodyType.Static)
                {
                    mScene!.Simulation.Bodies[mBody].Activity.SleepThreshold = mSleepThreshold;
                }
            }
        }

        public BodyHandle Handle => mBody;

        public void PrePhysicsUpdate()
        {
            using var prePhysicsUpdateEvent = OptickMacros.Event();

            if (mScene!.TryGetComponent(mEntity, out TransformComponent? transform))
            {
                var simulation = mScene.Simulation;
                if (mBodyType != BodyType.Static)
                {
                    var body = simulation.Bodies[mBody];

                    body.Pose.Position = transform.Translation;
                    body.Pose.Orientation = transform.RotationQuat;

                    body.Awake = true;
                    body.UpdateBounds();
                }
                else
                {
                    var staticRef = simulation.Statics[mStatic];

                    staticRef.Pose.Position = transform.Translation;
                    staticRef.Pose.Orientation = transform.RotationQuat;

                    staticRef.UpdateBounds();
                }
            }

            mCollider!.Update();
        }

        public void PostPhysicsUpdate()
        {
            if (mBodyType == BodyType.Static)
            {
                return;
            }

            using var postPhysicsUpdateEvent = OptickMacros.Event();
            if (!mScene!.TryGetComponent(mEntity, out TransformComponent? transform))
            {
                return;
            }

            var simulation = mScene.Simulation;
            var body = simulation.Bodies[mBody];

            transform.Translation = body.Pose.Position;
            transform.RotationQuat = body.Pose.Orientation;
        }

        private readonly ColliderType mInitialColliderType;
        private Collider? mCollider;
        private bool mColliderInitialized;

        private TypedIndex mShapeIndex;
        private Func<float, BodyInertia>? mComputeInertia;

        private BodyHandle mBody;
        private StaticHandle mStatic;

        private BodyType mBodyType;
        private float mMass, mSleepThreshold;

        private Scene? mScene;
        private ulong mEntity;
    }
}