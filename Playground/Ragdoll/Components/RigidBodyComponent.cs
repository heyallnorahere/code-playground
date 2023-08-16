using BepuPhysics;
using BepuPhysics.Collidables;
using CodePlayground;
using ImGuiNET;
using Optick.NET;
using Ragdoll.Layers;
using Ragdoll.Physics;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Ragdoll.Components
{
    public enum BodyType
    {
        Dynamic,
        Kinematic,
        Static
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

            bool isBody = mBodyType != BodyType.Static;
            BodyTypeChanged?.Invoke(isBody ? mBody : null, isBody ? null : mStatic, mBodyType);
        }

        public BodyType BodyType
        {
            get => mBodyType;
            set
            {
                if (mBodyType == value)
                {
                    return;
                }

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

        public BodyHandle Body => mBodyType != BodyType.Static ? mBody : throw new InvalidOperationException();
        public StaticHandle Static => mBodyType == BodyType.Static ? mStatic : throw new InvalidOperationException();
        public event Action<BodyHandle?, StaticHandle?, BodyType>? BodyTypeChanged;

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