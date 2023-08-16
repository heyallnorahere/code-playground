using BepuPhysics;
using BepuPhysics.Collidables;
using System;
using System.Reflection;

namespace Ragdoll.Physics
{
    public enum ColliderType
    {
        Box,
        StaticModel
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
        protected void TriggerOnChanged(TypedIndex index, Func<float, BodyInertia> computeInertia) => OnChanged?.Invoke(index, computeInertia);

        public abstract void Initialize(Scene scene, ulong id);
        public abstract void Edit();
        public abstract void Cleanup();
        public abstract void Update();
        public abstract void Invalidate();
    }
}