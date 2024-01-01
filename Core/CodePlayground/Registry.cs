using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Varena;

namespace CodePlayground
{
    public delegate void RegistryHandleModifyCallback<T>(ref T data);
    public interface IRegistryHandle
    {
        public unsafe void* Address { get; }
        public object Value { get; }
        public bool HasValue { get; }
        public Type DataType { get; }

        public bool Modify<T>(RegistryHandleModifyCallback<T> callback);
        public void Modify(Action<object> callback);
    }

    public unsafe readonly struct RegistryHandle<T> : IRegistryHandle where T : unmanaged
    {
        internal RegistryHandle(T* pointer)
        {
            mPointer = pointer;
        }

        public bool Modify<U>(RegistryHandleModifyCallback<U> callback)
        {
            using var modifyEvent = OptickMacros.Event(category: Category.Scene);

            if (*mPointer is not U argument)
            {
                return false;
            }

            callback.Invoke(ref argument);
            *mPointer = (T)(object)argument!;

            return true;
        }

        void IRegistryHandle.Modify(Action<object> callback)
        {
            using var modifyEvent = OptickMacros.Event(category: Category.Scene);

            ref T reference = ref Unsafe.AsRef<T>(mPointer);
            object data = reference;

            callback.Invoke(data);
            reference = (T)data;
        }

        public readonly ref T Value => ref *mPointer;
        public readonly bool HasValue => mPointer is not null;

        object IRegistryHandle.Value => Value;
        void* IRegistryHandle.Address => mPointer;
        Type IRegistryHandle.DataType => typeof(T);

        private readonly T* mPointer;
    }

    internal interface IRegistryComponentSystem : IDisposable
    {
        public IRegistryHandle Add(ulong entity, object data);
        public IRegistryHandle Get(ulong entity);
        public bool TryGet(ulong entity, out IRegistryHandle? handle);
        public bool Remove(ulong entity);
    }

    internal sealed class RegistryComponentSystem<T> : IRegistryComponentSystem where T : unmanaged
    {
        public RegistryComponentSystem()
        {
            var manager = Application.ArenaManager;

            mArray = manager.CreateArray<T>(typeof(T).Name, Registry.AllocationSize);
            mComponents = new Dictionary<ulong, nint>();
            mFreeIndices = new Queue<nint>();
        }

        public void Dispose() => mArray.Dispose();

        IRegistryHandle IRegistryComponentSystem.Add(ulong entity, object data) => Add(entity, (T)data);
        public unsafe RegistryHandle<T> Add(ulong entity, T data)
        {
            using var addEvent = OptickMacros.Event(category: Category.Scene);

            if (!mFreeIndices.TryDequeue(out nint address))
            {
                mArray.Append(data);
                address = (nint)Unsafe.AsPointer(ref mArray[^1]);
            }
            else
            {
                ref var reference = ref Unsafe.AsRef<T>((void*)address);
                reference = data;
            }

            mComponents.Add(entity, address);
            return new RegistryHandle<T>((T*)address);
        }

        IRegistryHandle IRegistryComponentSystem.Get(ulong entity) => Get(entity);
        public unsafe RegistryHandle<T> Get(ulong entity)
        {
            using var getEvent = OptickMacros.Event(category: Category.Scene);

            nint pointer = mComponents[entity];
            return new RegistryHandle<T>((T*)pointer);
        }

        bool IRegistryComponentSystem.TryGet(ulong entity, out IRegistryHandle? handle)
        {
            if (!TryGet(entity, out RegistryHandle<T> typedHandle))
            {
                handle = null;
                return false;
            }

            handle = typedHandle;
            return true;
        }

        public unsafe bool TryGet(ulong entity, out RegistryHandle<T> handle)
        {
            using var tryGetEvent = OptickMacros.Event(category: Category.Scene);

            if (!mComponents.TryGetValue(entity, out nint address))
            {
                handle = default;
                return false;
            }

            handle = new RegistryHandle<T>((T*)address);
            return true;
        }

        public bool Remove(ulong entity)
        {
            using var removeEvent = OptickMacros.Event(category: Category.Scene);

            if (!mComponents.TryGetValue(entity, out nint address))
            {
                return false;
            }

            mComponents.Remove(entity);
            mFreeIndices.Enqueue(address);

            return true;
        }

        private readonly VirtualArray<T> mArray;
        private readonly Dictionary<ulong, nint> mComponents;
        private readonly Queue<nint> mFreeIndices;
    }

    public sealed class Registry : IEnumerable<ulong>, IDisposable
    {
        public const ulong Null = 0;
        public const uint AllocationSize = 1 << 15; // 32 kilobytes

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
            mSystems = new Dictionary<int, IRegistryComponentSystem>();
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

            foreach (var componentSet in mSystems.Values)
            {
                componentSet.Remove(id);
            }

            mEntities.Remove(id);
        }

        public IEnumerable<IRegistryHandle> View(ulong id)
        {
            using var viewEvent = Profiler.Event();

            if (!mEntities.TryGetValue(id, out ulong flags))
            {
                throw new InvalidOperationException($"No such entity: {id}");
            }

            var components = new List<IRegistryHandle>();
            for (int i = 0; i < 64; i++)
            {
                ulong mask = 1ul << i;
                if ((flags & mask) != mask)
                {
                    continue;
                }

                components.Add(mSystems[i].Get(id));
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

        public bool Has<T>(ulong id) where T : unmanaged => Has(id, typeof(T));
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

        public bool TryGet<T>(ulong id, [NotNullWhen(true)] out RegistryHandle<T> handle) where T : unmanaged
        {
            using var tryGetEvent = Profiler.Event();

            if (TryGet(id, typeof(T), out IRegistryHandle? genericHandle))
            {
                handle = (RegistryHandle<T>)genericHandle;
                return true;
            }

            handle = default;
            return false;
        }

        public bool TryGet(ulong id, Type type, [NotNullWhen(true)] out IRegistryHandle? component)
        {
            using var tryGetEvent = Profiler.Event();

            if (!mComponentTypeIDs.TryGetValue(type, out int bit))
            {
                component = null;
                return false;
            }

            if (!mSystems.TryGetValue(bit, out IRegistryComponentSystem? system))
            {
                component = null;
                return false;
            }

            return system.TryGet(id, out component);
        }

        public RegistryHandle<T> Get<T>(ulong id) where T : unmanaged => (RegistryHandle<T>)Get(id, typeof(T));
        public IRegistryHandle Get(ulong id, Type type)
        {
            using var getEvent = Profiler.Event();

            if (TryGet(id, type, out IRegistryHandle? component))
            {
                return component;
            }

            throw new ArgumentException($"No component of type {type} exists on this entity!");
        }

        public RegistryHandle<T> Add<T>(ulong id, params object?[] args) where T : unmanaged => (RegistryHandle<T>)Add(id, typeof(T), args);
        public IRegistryHandle Add(ulong id, Type type, params object?[] args)
        {
            var component = Utilities.CreateDynamicInstance(type, args);
            return Add(id, component);
        }

        public IRegistryHandle Add(ulong id, object component)
        {
            using var addEvent = Profiler.Event();

            VerifyUnlocked();
            if (!mEntities.TryGetValue(id, out ulong flags))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            var componentType = component.GetType();
            if (!componentType.IsValueType)
            {
                throw new ArgumentException("Only value-type objects can be used as components!");
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

            if (!mSystems.TryGetValue(bit, out IRegistryComponentSystem? system))
            {
                var type = typeof(RegistryComponentSystem<>).MakeGenericType(componentType);

                system = (IRegistryComponentSystem)Utilities.CreateDynamicInstance(type);
                mSystems.Add(bit, system);
            }

            mEntities[id] = flags | mask;
            return system.Add(id, component);
        }

        public void Remove(ulong id, Type type)
        {
            using var removeEvent = Profiler.Event();

            VerifyUnlocked();
            if (!mComponentTypeIDs.TryGetValue(type, out int bit))
            {
                return;
            }

            if (!mSystems.TryGetValue(bit, out IRegistryComponentSystem? system))
            {
                return;
            }

            ulong mask = 1ul << bit;
            if (!mEntities.TryGetValue(id, out ulong flags) || (flags & mask) != mask)
            {
                return;
            }

            system.Remove(id);
            mEntities[id] = flags & ~mask;
        }

        public void Dispose() => Clear();
        public void Clear()
        {
            foreach (var system in mSystems.Values)
            {
                system.Dispose();
            }

            mEntities.Clear();
            mSystems.Clear();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IEnumerator<ulong> GetEnumerator() => mEntities.Keys.GetEnumerator();

        public int Count => mEntities.Count;

        public bool Locked => mLockCount > 0;
        public ulong CurrentID
        {
            get => mCurrentID;
            set => mCurrentID = value;
        }

        private ulong mCurrentID;
        private int mLockCount;

        private readonly Dictionary<ulong, ulong> mEntities;
        private readonly Dictionary<int, IRegistryComponentSystem> mSystems;
        private readonly Dictionary<Type, int> mComponentTypeIDs;
    }
}