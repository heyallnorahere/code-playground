using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CodePlayground;
using Ragdoll.Components;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Ragdoll
{
    public enum ComponentEventID
    {
        Added,
        Removed,
        Edited,
        Collision,
        PrePhysicsUpdate,
        PostPhysicsUpdate,
        PreBoneUpdate
    }

    public struct ComponentEventInfo
    {
        public Scene Scene { get; set; }
        public ulong Entity { get; set; }
        public ComponentEventID Event { get; set; }
        public object? Context { get; set; }
    }

    public struct VelocityDamping
    {
        public float Linear;
        public float Angular;
    }

    public enum LightType
    {
        Point,
        // todo: implement more light types
    }

    public sealed class ComponentEventDispatcher
    {
        public ComponentEventDispatcher(ComponentEventInfo eventInfo)
        {
            mEventInfo = eventInfo;
            mHandled = false;
        }

        public void Dispatch(ComponentEventID eventID, Action handler)
        {
            if (mEventInfo.Event != eventID)
            {
                return;
            }

            handler.Invoke();
            mHandled = true;
        }

        public void Dispatch(ComponentEventID eventID, Func<bool> handler)
        {
            if (mEventInfo.Event != eventID)
            {
                return;
            }

            mHandled |= handler.Invoke();
        }

        public void Dispatch(ComponentEventID eventID, Action<Scene, ulong> handler)
        {
            if (mEventInfo.Event != eventID)
            {
                return;
            }

            handler.Invoke(mEventInfo.Scene, mEventInfo.Entity);
            mHandled = true;
        }

        public void Dispatch(ComponentEventID eventID, Func<Scene, ulong, bool> handler)
        {
            if (mEventInfo.Event != eventID)
            {
                return;
            }

            mHandled |= handler.Invoke(mEventInfo.Scene, mEventInfo.Entity);
        }

        public void Dispatch(ComponentEventID eventID, Action<Scene, ulong, object?> handler)
        {
            if (mEventInfo.Event != eventID)
            {
                return;
            }

            handler.Invoke(mEventInfo.Scene, mEventInfo.Entity, mEventInfo.Context);
            mHandled = true;
        }

        public void Dispatch(ComponentEventID eventID, Func<Scene, ulong, object?, bool> handler)
        {
            if (mEventInfo.Event != eventID)
            {
                return;
            }

            mHandled |= handler.Invoke(mEventInfo.Scene, mEventInfo.Entity, mEventInfo.Context);
        }

        public bool Handled => mHandled;
        public static implicit operator bool(ComponentEventDispatcher dispatcher) => dispatcher.mHandled;

        private bool mHandled;
        private readonly ComponentEventInfo mEventInfo;
    }

    internal struct SceneSimulationCallbacks : INarrowPhaseCallbacks, IPoseIntegratorCallbacks
    {
        public SceneSimulationCallbacks(Scene scene)
        {
            mInitialized = false;
            mScene = scene;
        }

        public void Dispose()
        {
            if (!mInitialized)
            {
                return;
            }

            // todo: dispose
            mInitialized = false;
        }

        public void Initialize(Simulation simulation)
        {
            // todo: initialize
        }

        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        {
            return a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
        }

        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        {
            return true;
        }

        public unsafe bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial.FrictionCoefficient = 1f;
            pairMaterial.MaximumRecoveryVelocity = 2f;
            pairMaterial.SpringSettings = new SpringSettings
            {
                Frequency = 30f,
                DampingRatio = 1f
            };

            return true;
        }

        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
        {
            return true;
        }

        public void PrepareForIntegration(float dt)
        {
            mLinearDamping = new Vector<float>(MathF.Pow(float.Clamp(1f - mScene.VelocityDamping.Linear, 0f, 1f), dt));
            mAngularDamping = new Vector<float>(MathF.Pow(float.Clamp(1f - mScene.VelocityDamping.Angular, 0f, 1f), dt));
            mGravity = Vector3Wide.Broadcast(mScene.Gravity * dt);
        }

        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            velocity.Linear += mGravity;

            velocity.Linear *= mLinearDamping;
            velocity.Angular *= mAngularDamping;
        }

        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public bool AllowSubstepsForUnconstrainedBodies => false;
        public bool IntegrateVelocityForKinematics => false;

        private Vector<float> mLinearDamping, mAngularDamping;
        public Vector3Wide mGravity;

        private bool mInitialized;
        private readonly Scene mScene;
    }

    internal struct EntityCallbackData
    {
        public Dictionary<ulong, Action<object, bool>> Callbacks { get; set; }
    }

    public sealed class Scene : IDisposable
    {
        public const ulong Null = Registry.Null;
        public const string EntityDragDropID = "scene-entity";

        public Scene()
        {
            using var createdEvent = Profiler.Event();

            mDisposed = true;
            mRegistry = new Registry();

            mCallbackData = new Dictionary<ulong, EntityCallbackData>();
            mCurrentCallbackID = 0;

            mUpdatePhysics = true;
            mGravity = Vector3.UnitY * -9.81f;
            mVelocityDamping = new VelocityDamping
            {
                Linear = 0.3f,
                Angular = 0.3f
            };

            int threadCount = Environment.ProcessorCount;
            int targetThreadCount = int.Max(1, threadCount - (threadCount > 4 ? 2 : 1));
            mThreadDispatcher = new ThreadDispatcher(targetThreadCount);

            var callbacks = new SceneSimulationCallbacks(this);
            mBufferPool = new BufferPool();
            mSimulation = Simulation.Create(mBufferPool, callbacks, callbacks, new SolveDescription(6, 1));
        }

        ~Scene()
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
            using var disposeEvent = Profiler.Event();
            if (disposing)
            {
                mSimulation.Dispose();
                mBufferPool.Clear(); // equivalent to Dispose()
                mThreadDispatcher.Dispose();
            }
        }

        public void Update(double delta)
        {
            using var updateEvent = Profiler.Event();
            if (mUpdatePhysics)
            {
                using var physicsEvent = Profiler.Event("Update physics");
                var entityView = ViewEntities(typeof(RigidBodyComponent), typeof(TransformComponent));

                using (Profiler.Event("Pre-physics update"))
                {
                    foreach (var entity in entityView)
                    {
                        var rigidBody = GetComponent<RigidBodyComponent>(entity);
                        InvokeComponentEvent(rigidBody, entity, ComponentEventID.PrePhysicsUpdate, null);
                    }
                }

                using (Profiler.Event("Physics calculations"))
                {
                    mSimulation.Timestep((float)delta, null);
                }

                using (Profiler.Event("Post-physics update"))
                {
                    foreach (var entity in entityView)
                    {
                        var rigidBody = GetComponent<RigidBodyComponent>(entity);
                        InvokeComponentEvent(rigidBody, entity, ComponentEventID.PostPhysicsUpdate, null);
                    }
                }
            }

            // todo: some sort of script
        }

        public bool InvokeComponentEvent(object component, ulong id, ComponentEventID eventID, object? context = null)
        {
            using var eventEvent = Profiler.Event();

            var type = component.GetType();
            var method = type.GetMethod("OnEvent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, new Type[]
            {
                typeof(ComponentEventInfo),
            });

            if (method?.ReturnType != typeof(bool))
            {
                return false;
            }

            return (bool)method.Invoke(component, new object[]
            {
                new ComponentEventInfo
                {
                    Scene = this,
                    Entity = id,
                    Event = eventID,
                    Context = context
                }
            })!;
        }

        public IDisposable Lock() => mRegistry.Lock();
        public ulong NewEntity(string tag = "Entity")
        {
            using var newEvent = Profiler.Event();

            ulong id = mRegistry.New();
            mCallbackData.Add(id, new EntityCallbackData
            {
                Callbacks = new Dictionary<ulong, Action<object, bool>>()
            });

            AddComponent<TagComponent>(id, tag);
            return id;
        }

        public void DestroyEntity(ulong id)
        {
            using var destroyEvent = Profiler.Event();

            var types = ViewComponents(id).Select(component => component.GetType());
            foreach (var type in types)
            {
                RemoveComponent(id, type);
            }

            mCallbackData.Remove(id);
            mRegistry.Destroy(id);
        }

        public IEnumerable<ulong> Entities => mRegistry;
        public IEnumerable<object> ViewComponents(ulong id) => mRegistry.View(id);
        public IEnumerable<ulong> ViewEntities(params Type[] types) => mRegistry.View(types);

        public bool HasComponent<T>(ulong id) where T : class => mRegistry.Has<T>(id);
        public bool HasComponent(ulong id, Type type) => mRegistry.Has(id, type);

        public T GetComponent<T>(ulong id) where T : class => mRegistry.Get<T>(id);
        public object GetComponent(ulong id, Type type) => mRegistry.Get(id, type);

        public bool TryGetComponent<T>(ulong id, [NotNullWhen(true)] out T? component) where T : class => mRegistry.TryGet(id, out component);
        public bool TryGetComponent(ulong id, Type type, [NotNullWhen(true)] out object? component) => mRegistry.TryGet(id, type, out component);

        public T AddComponent<T>(ulong id, params object?[] args) where T : class => (T)AddComponent(id, typeof(T), args);
        public object AddComponent(ulong id, Type type, params object?[] args)
        {
            using var addEvent = Profiler.Event();
            var component = mRegistry.Add(id, type, args);

            using (Lock())
            {
                InvokeComponentEvent(component, id, ComponentEventID.Added);
                foreach (var callback in mCallbackData[id].Callbacks.Values)
                {
                    callback.Invoke(component, true);
                }
            }

            return component;
        }

        public void RemoveComponent<T>(ulong id) where T : class => RemoveComponent(id, typeof(T));
        public void RemoveComponent(ulong id, Type type)
        {
            using var removeEvent = Profiler.Event();
            if (!TryGetComponent(id, type, out object? component))
            {
                return;
            }

            using (Lock())
            {
                InvokeComponentEvent(component, id, ComponentEventID.Removed);
                foreach (var callback in mCallbackData[id].Callbacks.Values)
                {
                    callback.Invoke(component, false);
                }
            }

            mRegistry.Remove(id, type);
        }

        public ulong AddEntityComponentListener(ulong entity, Action<object, bool> callback)
        {
            using var addListenerEvent = Profiler.Event();

            ulong id = mCurrentCallbackID++;
            mCallbackData[entity].Callbacks.Add(id, callback);

            return id;
        }

        public bool RemoveEntityComponentListener(ulong entity, ulong callback)
        {
            using var removeListenerEvent = Profiler.Event();

            var callbacks = mCallbackData[entity].Callbacks;
            if (!callbacks.ContainsKey(callback))
            {
                return false;
            }

            callbacks.Remove(callback);
            return true;
        }

        public string GetDisplayedEntityTag(ulong id)
        {
            using var getTagEvent = Profiler.Event();
            if (TryGetComponent(id, out TagComponent? tag))
            {
                return tag.Tag;
            }

            return $"<no tag:{id}>";
        }

        public ref bool UpdatePhysics => ref mUpdatePhysics;
        public Simulation Simulation => mSimulation;

        public ref Vector3 Gravity => ref mGravity;
        public ref VelocityDamping VelocityDamping => ref mVelocityDamping;

        private bool mUpdatePhysics;
        private readonly Simulation mSimulation;
        private BufferPool mBufferPool;
        private ThreadDispatcher mThreadDispatcher;

        private Vector3 mGravity;
        private VelocityDamping mVelocityDamping;

        private readonly Dictionary<ulong, EntityCallbackData> mCallbackData;
        private ulong mCurrentCallbackID;

        private readonly Registry mRegistry;
        private bool mDisposed;
    }
}