using BepuPhysics;
using CodePlayground;
using ImGuiNET;
using Ragdoll.Layers;
using Ragdoll.Physics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Physics constraint")]
    public sealed class PhysicsConstraintComponent
    {
        private static readonly Dictionary<ConstraintType, ConstructorInfo> sRegisteredConstraints;
        private static ConstraintType? sUIConstraintType;

        static PhysicsConstraintComponent()
        {
            sRegisteredConstraints = new Dictionary<ConstraintType, ConstructorInfo>();
            sUIConstraintType = null;

            var assembly = Assembly.GetExecutingAssembly();
            var types = assembly.GetTypes();

            foreach (var type in types)
            {
                if (!type.Extends<Constraint>())
                {
                    continue;
                }

                var attribute = type.GetCustomAttribute<RegisteredConstraintAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Array.Empty<Type>());
                if (constructor is null)
                {
                    throw new ArgumentException($"No suitable constructor found for type {type}!");
                }

                sRegisteredConstraints.Add(attribute.Type, constructor);
            }
        }

        public PhysicsConstraintComponent()
        {
            mEntityA = mEntityB = Scene.Null;
            mComponentCallbacks = new Dictionary<ulong, ulong>();
            mConstraints = new Dictionary<ConstraintType, Constraint>();
        }

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            var dispatcher = new ComponentEventDispatcher(eventInfo);

            dispatcher.Dispatch(ComponentEventID.Added, OnComponentAdded);
            dispatcher.Dispatch(ComponentEventID.Removed, OnComponentRemoved);
            dispatcher.Dispatch(ComponentEventID.Edited, OnEdit);

            return dispatcher;
        }

        private void OnComponentAdded(Scene scene, ulong id)
        {
            mScene = scene;
        }

        private void OnComponentRemoved()
        {
            if (mScene is null)
            {
                throw new InvalidOperationException("This should not occur!");
            }

            DestroyAll();
            foreach (var entity in mComponentCallbacks.Keys)
            {
                mScene.RemoveEntityComponentListener(entity, mComponentCallbacks[entity]);
                if (mScene.TryGetComponent(entity, out RigidBodyComponent? rigidBody))
                {
                    rigidBody.BodyTypeChanged -= BodyTypeChangedListener;
                }
            }
        }

        private void OnEdit()
        {
            bool invalidate = false;

            invalidate |= ImGuiUtilities.DragDropEntityTarget("Entity A", mScene!, ref mEntityA);
            invalidate |= ImGuiUtilities.DragDropEntityTarget("Entity B", mScene!, ref mEntityB);

            if (invalidate)
            {
                Invalidate(false);
            }

            var style = ImGui.GetStyle();
            var font = ImGui.GetFont();

            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f));
            float lineHeight = font.FontSize + style.FramePadding.Y * 2f;

            if (ImGui.BeginCombo("##constraint-type", sUIConstraintType?.ToString() ?? "--None--"))
            {
                var types = Enum.GetValues<ConstraintType>();
                foreach (var type in types)
                {
                    bool isSelected = type == sUIConstraintType;
                    bool isDisabled = !sRegisteredConstraints.ContainsKey(type);

                    if (isDisabled)
                    {
                        ImGui.BeginDisabled();
                    }

                    var name = type.ToString();
                    if (ImGui.Selectable(name, isSelected))
                    {
                        sUIConstraintType = type;
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

            ImGui.PopStyleVar();
            ImGui.SameLine();

            if (sUIConstraintType is null)
            {
                ImGui.BeginDisabled();
            }

            bool constraintExists = sUIConstraintType is null ? false : mConstraints.ContainsKey(sUIConstraintType.Value);
            if (ImGui.Button(constraintExists ? "X" : "+", new Vector2(lineHeight)))
            {
                var constraintType = sUIConstraintType!.Value;
                if (constraintExists)
                {
                    RemoveConstraint(constraintType);
                }
                else
                {
                    Constraint(constraintType);
                }
            }

            if (sUIConstraintType is null)
            {
                ImGui.EndDisabled();
            }
            else
            {
                var constraintType = sUIConstraintType!.Value;
                if (mConstraints.TryGetValue(constraintType, out Constraint? constraint))
                {
                    constraint.Edit();
                }
            }
        }

        public Constraint? Constraint(ConstraintType type)
        {
            if (!mConstraints.TryGetValue(type, out Constraint? constraint))
            {
                if (!sRegisteredConstraints.TryGetValue(type, out ConstructorInfo? constructor))
                {
                    return null;
                }

                constraint = (Constraint)constructor.Invoke(null);
                mConstraints.Add(type, constraint);

                if (mConstraintsInitialized)
                {
                    InitializeAll();
                }
            }

            return constraint;
        }

        public bool ConstraintExists(ConstraintType type) => mConstraints.ContainsKey(type);
        public bool RemoveConstraint(ConstraintType type)
        {
            if (!mConstraints.TryGetValue(type, out Constraint? constraint))
            {
                return false;
            }

            mConstraints.Remove(type);
            if (constraint.IsInitialized)
            {
                constraint.Destroy();
            }

            return true;
        }

        public void Invalidate(bool force)
        {
            if (mScene is null)
            {
                return;
            }

            var existingEntities = new HashSet<ulong>(mComponentCallbacks.Keys);
            var currentEntities = new HashSet<ulong>
            {
                mEntityA, mEntityB
            };

            currentEntities.Remove(Scene.Null);

            var newEntities = new HashSet<ulong>(currentEntities);
            var oldEntities = new HashSet<ulong>(existingEntities);

            newEntities.ExceptWith(existingEntities);
            oldEntities.ExceptWith(currentEntities);

            foreach (var old in oldEntities)
            {
                mScene.RemoveEntityComponentListener(old, mComponentCallbacks[old]);
                mComponentCallbacks.Remove(old);

                if (mScene.TryGetComponent(old, out RigidBodyComponent? rigidBody))
                {
                    rigidBody.BodyTypeChanged -= BodyTypeChangedListener;
                }
            }

            foreach (var entity in newEntities)
            {
                mScene.AddEntityComponentListener(entity, EntityComponentListener);
            }

            if (oldEntities.Count > 0 || force)
            {
                DestroyAll();
            }

            var rigidBodies = currentEntities.Where(entity =>
            {
                if (!mScene.TryGetComponent(entity, out RigidBodyComponent? rigidBody))
                {
                    return false;
                }

                return rigidBody.BodyType != BodyType.Static;
            }).ToArray();

            if (rigidBodies.Length != 2 || (newEntities.Count == 0 && !force))
            {
                return;
            }

            InitializeAll();
        }

        private void EntityComponentListener(object component, bool exists)
        {
            if (component is not RigidBodyComponent rigidBody)
            {
                return;
            }

            if (exists)
            {
                rigidBody.BodyTypeChanged += BodyTypeChangedListener;
            }

            Invalidate(false);
        }

        private void BodyTypeChangedListener(BodyHandle? body, StaticHandle? staticHandle, BodyType type)
        {
            Invalidate(false); // lmao
        }

        private void DestroyAll()
        {
            foreach (var constraint in mConstraints.Values)
            {
                if (!constraint.IsInitialized)
                {
                    continue;
                }

                constraint.Destroy();
            }

            mConstraintsInitialized = false;
        }

        private void InitializeAll()
        {
            if (mScene is null)
            {
                return;
            }

            var rigidBodyA = mScene.GetComponent<RigidBodyComponent>(mEntityA);
            var rigidBodyB = mScene.GetComponent<RigidBodyComponent>(mEntityB);

            var simulation = mScene.Simulation;
            var bodyA = rigidBodyA.Body;
            var bodyB = rigidBodyB.Body;

            foreach (var constraint in mConstraints.Values)
            {
                if (constraint.IsInitialized)
                {
                    return;
                }

                constraint.Create(simulation, bodyA, bodyB);
            }

            mConstraintsInitialized = true;
        }

        private readonly Dictionary<ulong, ulong> mComponentCallbacks;
        private ulong mEntityA, mEntityB;

        private readonly Dictionary<ConstraintType, Constraint> mConstraints;
        private bool mConstraintsInitialized;
        private Scene? mScene;
    }
}