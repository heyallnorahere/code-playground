using CodePlayground;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Ragdoll
{
    internal struct EntityData
    {
        public ulong Previous { get; set; }
        public ulong Next { get; set; }
        public int ComponentOffset { get; set; }
        public Dictionary<Type, int> ComponentIndices { get; set; }
    }

    public sealed class Registry : IEnumerable<ulong>
    {
        public const ulong Null = 0;

        public Registry()
        {
            mCurrentID = Null;
            mEntities = new Dictionary<ulong, EntityData>();
            mComponents = new List<object>();
        }

        public ulong New()
        {
            ulong id = ++mCurrentID;

            // expensive operation - though tbf so is creating an entity
            var ids = mEntities.Keys.ToList();
            ids.Sort();

            ulong previous = ids.LastOrDefault(Null);
            if (previous != Null)
            {
                var previousData = mEntities[previous];
                previousData.Next = id;
                mEntities[previous] = previousData;
            }

            mEntities.Add(id, new EntityData
            {
                Previous = previous,
                Next = Null,
                ComponentOffset = mComponents.Count,
                ComponentIndices = new Dictionary<Type, int>()
            });

            return id;
        }

        private int GetComponentOffset(ulong id)
        {
            if (id != Null)
            {
                var data = mEntities[id];
                return data.ComponentOffset;
            }
            else
            {
                return mComponents.Count;
            }
        }

        public bool Exists(ulong id) => mEntities.ContainsKey(id);
        public void Destroy(ulong id)
        {
            if (!mEntities.TryGetValue(id, out EntityData data))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            int offset = data.ComponentOffset;
            int nextOffset = GetComponentOffset(data.Next);

            mComponents.RemoveRange(offset, nextOffset - offset);
            mEntities.Remove(id);

            if (data.Previous != Null)
            {
                var previousData = mEntities[data.Previous];
                previousData.Next = data.Next;
                mEntities[data.Previous] = previousData;
            }

            if (data.Next != Null)
            {
                var nextData = mEntities[data.Next];
                nextData.Previous = data.Previous;
                mEntities[data.Next] = nextData;
            }
        }

        public IEnumerable<object> View(ulong id)
        {
            if (!mEntities.TryGetValue(id, out EntityData data))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            int offset = data.ComponentOffset;
            int nextOffset = GetComponentOffset(data.Next);

            var components = new object[nextOffset - offset];
            for (int i = 0; i < components.Length; i++)
            {
                components[i] = mComponents[offset + i];
            }

            return components;
        }

        public IEnumerable<ulong> View(params Type[] types)
        {
            var entities = new List<ulong>();
            foreach (ulong id in mEntities.Keys)
            {
                bool valid = true;
                foreach (var type in types)
                {
                    if (!Has(id, type))
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                {
                    entities.Add(id);
                }
            }

            return entities;
        }

        public bool Has<T>(ulong id) where T : class => Has(id, typeof(T));
        public bool Has(ulong id, Type type)
        {
            if (!mEntities.TryGetValue(id, out EntityData data))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            return data.ComponentIndices.ContainsKey(type);
        }

        public bool TryGet<T>(ulong id, [NotNullWhen(true)] out T? component) where T : class
        {
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
            if (!mEntities.TryGetValue(id, out EntityData data))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            if (!data.ComponentIndices.TryGetValue(type, out int index))
            {
                component = null;
                return false;
            }

            component = mComponents[data.ComponentOffset + index];
            return true;
        }

        public T Get<T>(ulong id) where T : class => (T)Get(id, typeof(T));
        public object Get(ulong id, Type type)
        {
            if (TryGet(id, type, out object? component))
            {
                return component;
            }

            throw new ArgumentException($"No component of type {type} exists on this entity!");
        }

        public void Add<T>(ulong id, params object?[] args) where T : class
        {
            var component = Utilities.CreateDynamicInstance<T>(args);
            Add(id, component);
        }

        public void Add(ulong id, Type type, params object?[] args)
        {
            var component = Utilities.CreateDynamicInstance(type, args);
            Add(id, component);
        }

        public void Add(ulong id, object component)
        {
            if (!mEntities.TryGetValue(id, out EntityData data))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            var type = component.GetType();
            if (type.IsValueType)
            {
                throw new ArgumentException("Only pass-by-reference objects can be used as components!");
            }

            if (data.ComponentIndices.ContainsKey(type))
            {
                throw new ArgumentException($"A component of type {type} already exists on this entity!");
            }

            int nextOffset = GetComponentOffset(data.Next);
            mComponents.Insert(nextOffset, component);
            data.ComponentIndices.Add(type, nextOffset - data.ComponentOffset);

            ulong current = data.Next;
            while (current != Null)
            {
                var currentData = mEntities[current];
                currentData.ComponentOffset++;
                mEntities[current] = currentData;

                current = currentData.Next;
            }
        }

        public void Remove(ulong id, Type type)
        {
            if (!mEntities.TryGetValue(id, out EntityData data))
            {
                throw new ArgumentException("Invalid entity ID!");
            }

            if (!data.ComponentIndices.TryGetValue(type, out int index))
            {
                // not that big of a deal - just return
                return;
            }

            int offset = data.ComponentOffset;
            mComponents.RemoveAt(offset + index);
            data.ComponentIndices.Remove(type);

            ulong current = data.Next;
            while (current != Null)
            {
                var currentData = mEntities[current];
                currentData.ComponentOffset--;
                mEntities[current] = currentData;

                current = currentData.Next;
            }
        }

        public void Clear()
        {
            mEntities.Clear();
            mComponents.Clear();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IEnumerator<ulong> GetEnumerator() => mEntities.Keys.GetEnumerator();

        private ulong mCurrentID;
        private readonly Dictionary<ulong, EntityData> mEntities;
        private readonly List<object> mComponents;
    }
}