using BepuPhysics;
using BepuPhysics.Constraints;
using ImGuiNET;
using Optick.NET;
using System;
using System.Reflection;

namespace Ragdoll.Physics
{
    public enum ConstraintType
    {
        DistanceServo
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class RegisteredConstraintAttribute : Attribute
    {
        public RegisteredConstraintAttribute(ConstraintType type)
        {
            Type = type;
        }

        public ConstraintType Type { get; }
    }

    public abstract class Constraint
    {
        public Constraint()
        {
            var type = GetType();
            var attribute = type.GetCustomAttribute<RegisteredConstraintAttribute>();
            Type = attribute!.Type;
        }

        public ConstraintType Type { get; }
        public abstract bool IsInitialized { get; }

        public abstract void Create(Simulation simulation, BodyHandle bodyA, BodyHandle bodyB);
        public abstract void Destroy();

        public abstract void Edit();
        public abstract void Update();

        public event Action<ConstraintHandle>? OnChanged;
        protected void TriggerOnChanged(ConstraintHandle handle) => OnChanged?.Invoke(handle);
    }

    public static class ConstraintEditUtilities
    {
        private const float NoMaximum = float.MaxValue / int.MaxValue;

        public static bool EditServoSettings(ref ServoSettings settings)
        {
            using var editEvent = OptickMacros.Event();
            bool changed = false;

            changed |= ImGui.DragFloat("Maximum speed", ref settings.MaximumSpeed, 5f, 0f, NoMaximum);
            changed |= ImGui.DragFloat("Base speed", ref settings.BaseSpeed, 5f, 0f, NoMaximum);
            changed |= ImGui.DragFloat("Maximum force", ref settings.MaximumForce, 5f, 0f, NoMaximum);

            return changed;
        }

        public static bool EditSpringSettings(ref SpringSettings settings)
        {
            using var editEvent = OptickMacros.Event();
            bool changed = false;

            float frequency = settings.Frequency;
            if (ImGui.DragFloat("Frequency", ref frequency, 0.01f, 0.01f, NoMaximum))
            {
                settings.Frequency = frequency;
                changed = true;
            }

            float dampingRatio = settings.DampingRatio;
            if (ImGui.DragFloat("Damping ratio", ref dampingRatio, 0.01f, 0f, NoMaximum))
            {
                settings.DampingRatio = dampingRatio;
                changed = true;
            }

            return changed;
        }
    }
}