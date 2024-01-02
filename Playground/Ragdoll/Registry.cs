using CodePlayground;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

// temporary fix - this version is from an earlier commit
// i know this is bad practice - please forgive me future nora
namespace Ragdoll
{
    public sealed class Registry : IEnumerable<ulong>
    {
        public const ulong Null = 0;

        private sealed class RegistryLock : IDisposable
        {
            internal RegistryLock(Registry registry)
            {
                mRegistry = registry;
                mDisposed = false;

                mRegistry.mLockCount++;
            }

            ~RegistryLock()
            {
                if (!mDisposed)
                {
                    Dispose(false);
                    mDisposed = true;
                }
            }

            public void Dispose()
            {
                if (mDisposed)
                {
                    return;
                }

                Dispose(true);
                mDisposed = true;
            }

            private void Dispose(bool disposing)
            {
                mRegistry.mLockCount--;
            }

            private readonly Registry mRegistry;
            private bool mDisposed;
        }

        public Registry()
        {
            mCurrentID = Null;
            mLockCount = 0;
            mEntities = new Dictionary<ulong, ulong>();
            mComponents = new Dictionary<int, Dictionary<ulong, object>>();
            mComponentTypeIDs = new Dictionary<Type, int>();
        }

        public IDisposable Lock() => new RegistryLock(this);
        private void VerifyUnlocked()
        {
            if (mLockCount <= 0)
            {
                return;
            }

            throw new InvalidOperationException("The registry is currently locked!");
        }

        public ulong New()
        {
            using var newEvent = Profiler.Event();

            VerifyUnlocked();
            ulong id = ++mCurrentID;

            mEntities.Add(id, 0);
            return id;
        }

        public bool Exists(ulong id) => mEntities.ContainsKey(id);
        public void Destroy(ulong id)
        {
            using var destroyEvent = Profiler.Event();

            VerifyUnlocked();
            if (!mEntities.ContainsKey(id))
            {
                return;
            }

            foreach (var componentSet in mComponents.Values)
            {
                componentSet.Remove(id);
            }

            mEntities.Remove(id);
        }

        public IEnumerable<object> View(ulong id)
        {
            using var viewEvent = Profiler.Event();

            if (!mEntities.TryGetValue(id, out ulong flags))
            {
                throw new InvalidOperationException($"No such entity: {id}");
            }

            var components = new List<object>();
            for (int i = 0; i < 64; i++)
            {
                ulong mask = 1ul << i;
                if ((flags & mask) != mask)
                {
                    continue;
                }

                components.Add(mComponents[i][id]);
            }

            return components;
        }

        public IEnumerable<ulong> View(params Type[] types)
        {
            using var viewEvent = Profiler.Event();

            ulong mask = 0;
            foreach (var type in types)
            {
                if (!mComponentTypeIDs.TryGetValue(type, out int bit))
                {
                    return Array.Empty<ulong>();
                }

                mask |= 1ul << bit;
            }

            var entities = new List<ulong>();
            foreach ((ulong id, ulong bitflag) in mEntities)
            {
                if ((bitflag & mask) != mask)
                {
                    continue;
                }

                entities.Add(id);
            }

            return entities;
        }

        public bool Has<T>(ulong id) where T : class => Has(id, typeof(T));
        public bool Has(ulong id, Type type)
        {
            using var hasEvent = Profiler.Event();

            if (!mEntities.TryGetValue(id, out ulong flags))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            if (!mComponentTypeIDs.TryGetValue(type, out int bit))
            {
                return false;
            }

            ulong mask = 1ul << bit;
            return (flags & mask) == mask;
        }

        public bool TryGet<T>(ulong id, [NotNullWhen(true)] out T? component) where T : class
        {
            using var tryGetEvent = Profiler.Event();

            if (TryGet(id, typeof(T), out object? componentObject))
            {
                component = (T)componentObject;
                return true;
            }

            component = default;
            return false;
        }

        public bool TryGet(ulong id, Type type, [NotNullWhen(true)] out object? component)
        {
            using var tryGetEvent = Profiler.Event();

            if (!mComponentTypeIDs.TryGetValue(type, out int bit))
            {
                component = null;
                return false;
            }

            if (!mComponents.TryGetValue(bit, out Dictionary<ulong, object>? components))
            {
                component = null;
                return false;
            }

            return components.TryGetValue(id, out component);
        }

        public T Get<T>(ulong id) where T : class => (T)Get(id, typeof(T));
        public object Get(ulong id, Type type)
        {
            using var getEvent = Profiler.Event();

            if (TryGet(id, type, out object? component))
            {
                return component;
            }

            throw new ArgumentException($"No component of type {type} exists on this entity!");
        }

        public T Add<T>(ulong id, params object?[] args) where T : class => (T)Add(id, typeof(T), args);
        public object Add(ulong id, Type type, params object?[] args)
        {
            var component = Utilities.CreateDynamicInstance(type, args);
            Add(id, component);

            return component;
        }

        public void Add(ulong id, object component)
        {
            using var addEvent = Profiler.Event();

            VerifyUnlocked();
            if (!mEntities.TryGetValue(id, out ulong flags))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            var componentType = component.GetType();
            if (componentType.IsValueType)
            {
                throw new ArgumentException("Only pass-by-reference objects can be used as components!");
            }

            if (!mComponentTypeIDs.TryGetValue(componentType, out int bit))
            {
                bit = mComponentTypeIDs.Count;
                mComponentTypeIDs.Add(componentType, bit);
            }

            ulong mask = 1ul << bit;
            if ((flags & mask) == mask)
            {
                throw new ArgumentException($"A component of type {componentType} already exists on this entity!");
            }

            if (!mComponents.TryGetValue(bit, out Dictionary<ulong, object>? components))
            {
                components = new Dictionary<ulong, object>();
                mComponents.Add(bit, components);
            }

            components.Add(id, component);
            mEntities[id] = flags | mask;
        }

        public void Remove(ulong id, Type type)
        {
            using var removeEvent = Profiler.Event();

            VerifyUnlocked();
            if (!mComponentTypeIDs.TryGetValue(type, out int bit))
            {
                return;
            }

            if (!mComponents.TryGetValue(bit, out Dictionary<ulong, object>? components))
            {
                return;
            }

            ulong mask = 1ul << bit;
            if (!mEntities.TryGetValue(id, out ulong flags) || (flags & mask) != mask)
            {
                return;
            }

            components.Remove(id);
            mEntities[id] = flags & ~mask;
        }

        public void Clear()
        {
            mEntities.Clear();
            mComponents.Clear();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IEnumerator<ulong> GetEnumerator() => mEntities.Keys.GetEnumerator();

        public bool Locked => mLockCount > 0;
        public int Count => mEntities.Count;

        private ulong mCurrentID;
        private int mLockCount;
        private readonly Dictionary<ulong, ulong> mEntities;
        private readonly Dictionary<int, Dictionary<ulong, object>> mComponents;
        private readonly Dictionary<Type, int> mComponentTypeIDs;
    }
}