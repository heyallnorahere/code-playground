using BepuPhysics;
using BepuPhysics.Collidables;
using CodePlayground;
using CodePlayground.Graphics;
using ImGuiNET;
using Ragdoll.Layers;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Ragdoll.Components
{
    public enum ColliderType
    {
        Box
    }

    public enum BodyType
    {
        Dynamic,
        Kinematic
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

        public event Action<TypedIndex, IConvexShape>? OnChanged;

        protected void TriggerOnChanged(TypedIndex index, IConvexShape shape)
        {
            OnChanged?.Invoke(index, shape);
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
            mScene!.Simulation.Shapes.Remove(mIndex);
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
            var oldIndex = mIndex;
            var simulation = mScene!.Simulation;

            var size = mSize;
            if (mScene!.TryGetComponent(mEntity, out TransformComponent? transform))
            {
                size *= mCurrentScale = transform.Scale;
            }

            var shape = new Box(size.X, size.Y, size.Z);
            mIndex = simulation.Shapes.Add(shape);
            TriggerOnChanged(mIndex, shape);

            if (removeOld)
            {
                simulation.Shapes.Remove(oldIndex);
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
            mShapeIndex = new TypedIndex(-1, -1);
            mShape = null;

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

        private void OnColliderChanged(TypedIndex index, IConvexShape shape)
        {
            mShapeIndex = index;
            mShape = shape;
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

        private void OnComponentAdded(Scene scene, ulong id)
        {
            var simulation = scene.Simulation;

            mScene = scene;
            mEntity = id;

            CreateCollider(mColliderType);

            mScene.TryGetComponent<TransformComponent>(mEntity, out TransformComponent? transform);
            var rigidPose = new RigidPose
            {
                Position = transform?.Translation ?? Vector3.Zero,
                Orientation = transform?.CalculateQuaternion() ?? Quaternion.Zero
            };

            var body = BodyDescription.CreateDynamic(rigidPose, mShape!.ComputeInertia(Mass), mShapeIndex, SleepThreshold);
            mHandle = simulation.Bodies.Add(body);
        }

        private void OnComponentRemoved()
        {
            CleanupCollider();
            mScene!.Simulation.Bodies.Remove(mHandle);
        }

        private void OnEdit()
        {
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
                updateMass = true;
            }

            if (updateMass && mShape is not null)
            {
                body.LocalInertia = mShape.ComputeInertia(mass);
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
            var simulation = mScene!.Simulation;
            var body = simulation.Bodies[mHandle];

            body.Activity.SleepThreshold = SleepThreshold;
            if (mShape is not null)
            {
                body.LocalInertia = mShape.ComputeInertia(Mass);
            }

            if (mScene.TryGetComponent<TransformComponent>(mEntity, out TransformComponent? transform))
            {
                body.Pose.Position = transform.Translation;
                body.Pose.Orientation = transform.CalculateQuaternion();
            }

            mCollider!.Update();
        }

        public void PostPhysicsUpdate()
        {
            if (!mScene!.TryGetComponent<TransformComponent>(mEntity, out TransformComponent? transform))
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

        private TypedIndex mShapeIndex;
        private IConvexShape? mShape;

        private BodyHandle mHandle;

        private Scene? mScene;
        private ulong mEntity;
    }
}