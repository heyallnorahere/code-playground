using CodePlayground.Graphics;
using ImGuiNET;
using Ragdoll.Components;
using Ragdoll.Shaders;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ragdoll.Layers
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    internal sealed class RegisteredComponentAttribute : Attribute
    {
        public RegisteredComponentAttribute()
        {
            DisplayName = string.Empty;
        }

        public string DisplayName { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    internal sealed class ImGuiMenuAttribute : Attribute
    {
        public ImGuiMenuAttribute(string path)
        {
            Path = path;
            Flags = ImGuiWindowFlags.None;
            Visible = true;
        }

        public string Path { get; }
        public ImGuiWindowFlags Flags { get; set; }
        public bool Visible { get; set; }
    }

    internal delegate void ImGuiMenuCallback(IEnumerable<ImGuiMenu> children);
    internal sealed class ImGuiMenu
    {
        public ImGuiMenu(string title, ImGuiWindowFlags flags, bool visible, ImGuiMenuCallback callback)
        {
            mVisible = visible;
            mTitle = title;
            mFlags = flags;
            mCallback = callback;
            mChildren = new List<ImGuiMenu>();
        }

        public void Render(bool enableVisibility = true)
        {
            bool visible;
            if (enableVisibility)
            {
                visible = ImGui.Begin(mTitle, ref mVisible, mFlags);
            }
            else
            {
                visible = ImGui.Begin(mTitle, mFlags);
            }

            if (!visible)
            {
                return;
            }

            mCallback.Invoke(mChildren);
            ImGui.End();
        }

        public ref bool Visible => ref mVisible;
        public string Title => mTitle;
        public IList<ImGuiMenu> Children => mChildren;

        private bool mVisible;
        private readonly string mTitle;
        private readonly ImGuiWindowFlags mFlags;
        private readonly ImGuiMenuCallback mCallback;
        private readonly List<ImGuiMenu> mChildren;
    }

    internal struct ImGuiMenuInfo
    {
        public ImGuiMenu? Menu { get; set; }
        public Dictionary<string, ImGuiMenuInfo> Children { get; set; }
    }

    internal struct ComponentTypeInfo
    {
        public Type Type { get; set; }
        public string DisplayName { get; set; }
    }

    internal struct LoadedModelInfo
    {
        public IPipeline[] Pipelines { get; set; }
        // not much else...
    }

    internal struct CameraBufferData
    {
        public Matrix4x4 ViewProjection;
    }

    internal struct PushConstantData
    {
        public Matrix4x4 Model;
        public int BoneOffset;
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    internal sealed class SceneLayer : Layer
    {
        private static IReadOnlyList<ComponentTypeInfo> sComponentTypes;
        static SceneLayer()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var types = assembly.GetTypes();

            var componentTypes = new List<ComponentTypeInfo>();
            foreach (var type in types)
            {
                var attribute = type.GetCustomAttribute<RegisteredComponentAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var displayName = attribute.DisplayName;
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = type.Name;
                }

                componentTypes.Add(new ComponentTypeInfo
                {
                    Type = type,
                    DisplayName = displayName
                });
            }

            sComponentTypes = componentTypes;
        }

        public SceneLayer()
        {
            mSelectedEntity = Registry.Null;
            mRegistry = new Registry();
            mMenus = LoadMenus();
            mModelPath = mModelName = mModelError = string.Empty;
            mLoadedModelInfo = new Dictionary<int, LoadedModelInfo>();
            mCameraBuffer = null;
            mReflectionView = null;

            for (int i = 0; i < 5; i++)
            {
                NewEntity($"Entity {i + 1}");
            }
        }

        #region Menu loading

        private IReadOnlyList<ImGuiMenu> LoadMenus()
        {
            var methods = GetType().GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            var menus = new Dictionary<string, ImGuiMenuInfo>();

            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<ImGuiMenuAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var path = attribute.Path.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (path.Length == 0)
                {
                    throw new ArgumentException("Cannot pass an empty path!");
                }

                var currentContainer = menus;
                var callback = (ImGuiMenuCallback)Delegate.CreateDelegate(typeof(ImGuiMenuCallback), method.IsStatic ? null : this, method);
                var menu = new ImGuiMenu(path[^1], attribute.Flags, attribute.Visible, callback);

                for (int i = 0; i < path.Length; i++)
                {
                    var segment = path[i];
                    if (!currentContainer.TryGetValue(segment, out ImGuiMenuInfo menuInfo))
                    {
                        currentContainer[segment] = menuInfo = new ImGuiMenuInfo
                        {
                            Menu = i < path.Length - 1 ? null : menu,
                            Children = new Dictionary<string, ImGuiMenuInfo>()
                        };
                    }
                    else if (i == path.Length - 1)
                    {
                        if (menuInfo.Menu is not null)
                        {
                            throw new InvalidOperationException($"Duplicate menu title: {segment}");
                        }

                        menuInfo.Menu = menu;
                        currentContainer[segment] = menuInfo;
                    }

                    currentContainer = menuInfo.Children;
                }
            }

            var result = new List<ImGuiMenu>();
            LinkMenus(result, menus);

            return result;
        }

        private void LinkMenus(IList<ImGuiMenu> result, IReadOnlyDictionary<string, ImGuiMenuInfo> children)
        {
            foreach (var child in children.Values)
            {
                var menu = child.Menu;
                if (menu is not null)
                {
                    result.Add(menu);
                }

                LinkMenus(menu?.Children ?? result, child.Children);
            }
        }

        #endregion
        #region Layer events

        public override void OnPushed()
        {
            var app = App.Instance;
            mReflectionView = app.Renderer!.Library.CreateReflectionView<ModelShader>();

            int bufferSize = mReflectionView.GetBufferSize(nameof(ModelShader.u_CameraBuffer));
            if (bufferSize < 0)
            {
                throw new InvalidOperationException("Failed to find camera buffer!");
            }

            mCameraBuffer = app.GraphicsContext!.CreateDeviceBuffer(DeviceBufferUsage.Uniform, bufferSize);
        }

        public override void OnPopped()
        {
            mCameraBuffer?.Dispose();
            foreach (var modelData in mLoadedModelInfo.Values)
            {
                foreach (var pipeline in modelData.Pipelines)
                {
                    pipeline.Dispose();
                }
            }
        }

        private static void ComputeCameraVectors(Vector3 angle, out Vector3 direction, out Vector3 up)
        {
            var radians = angle * MathF.PI / 180f;

            float pitch = radians.X;
            float yaw = radians.Y;
            float roll = radians.Z + MathF.PI / 2f;

            float directionPitch = MathF.Sin(roll) * pitch - MathF.Cos(roll) * yaw;
            float directionYaw = MathF.Sin(roll) * yaw - MathF.Cos(roll) * pitch;

            // todo: take roll into account
            direction = Vector3.Normalize(new Vector3
            {
                X = MathF.Cos(directionYaw) * MathF.Cos(directionPitch),
                Y = MathF.Sin(directionPitch),
                Z = MathF.Sin(directionYaw) * MathF.Cos(directionPitch)
            });

            up = Vector3.Normalize(new Vector3
            {
                X = MathF.Cos(yaw - MathF.PI / 2f) * MathF.Cos(roll),
                Y = MathF.Sin(roll),
                Z = MathF.Sin(yaw - MathF.PI / 2f) * MathF.Cos(roll)
            });
        }

        public override void OnUpdate(double delta)
        {
            // todo: update scene

            var app = App.Instance;
            var context = app.GraphicsContext;
            var registry = app.ModelRegistry;

            if (registry is not null)
            {
                foreach (ulong entity in ViewEntities(typeof(RenderedModelComponent)))
                {
                    var modelData = GetComponent<RenderedModelComponent>(entity);

                    var skeleton = modelData.Model?.Skeleton;
                    if (skeleton is null)
                    {
                        continue;
                    }

                    var boneTransforms = new Matrix4x4[skeleton.BoneCount];
                    var globalTransforms = new List<Matrix4x4>();
                    for (int i = 0; i < boneTransforms.Length; i++)
                    {
                        int parent = skeleton.GetParent(i);
                        var parentTransform = parent < 0 ? skeleton.GetParentTransform(i) : globalTransforms[parent];

                        var nodeTransform = skeleton.GetTransform(i) * modelData.BoneTransforms![i];
                        var globalTransform = parentTransform * nodeTransform;

                        var offsetMatrix = skeleton.GetOffsetMatrix(i);
                        var boneTransform = globalTransform * offsetMatrix;

                        globalTransforms.Add(globalTransform);
                        boneTransforms[i] = Matrix4x4.Transpose(boneTransform);
                    }

                    var boneBuffer = registry.Models[modelData.ID].BoneBuffer;
                    boneBuffer.CopyFromCPU(boneTransforms, modelData.BoneOffset * Marshal.SizeOf<Matrix4x4>());
                }
            }

            if (context is not null)
            {
                var camera = Registry.Null;
                var entityView = ViewEntities(typeof(TransformComponent), typeof(CameraComponent));

                foreach (ulong entity in entityView)
                {
                    var component = GetComponent<CameraComponent>(entity);
                    if (component.MainCamera)
                    {
                        camera = entity;
                        break;
                    }
                }

                if (camera != Registry.Null)
                {
                    // todo: use framebuffer
                    var swapchain = context.Swapchain;
                    var transform = GetComponent<TransformComponent>(camera);
                    var cameraData = GetComponent<CameraComponent>(camera);

                    float aspectRatio = (float)swapchain.Width / swapchain.Height;
                    float fov = cameraData.FOV * MathF.PI / 180f;
                    ComputeCameraVectors(transform.Rotation + cameraData.RotationOffset, out Vector3 direction, out Vector3 up);

                    var math = new MatrixMath(context);
                    var projection = math.Perspective(fov, aspectRatio, 0.1f, 100f);
                    var view = math.LookAt(transform.Translation, transform.Translation + direction, up);

                    mCameraBuffer?.MapStructure(mReflectionView!, nameof(ModelShader.u_CameraBuffer), new CameraBufferData
                    {
                        // GLSL matrices are column-major
                        // System.Numerics uses row-major
                        ViewProjection = Matrix4x4.Transpose(projection * view)
                    });
                }
            }
        }

        public override void OnImGuiRender()
        {
            foreach (var menu in mMenus)
            {
                menu.Render(false);
            }
        }

        // todo: move to prerender with a framebuffer
        public override void OnRender(Renderer renderer)
        {
            foreach (ulong id in ViewEntities(typeof(TransformComponent), typeof(RenderedModelComponent)))
            {
                var transform = GetComponent<TransformComponent>(id);
                var renderedModel = GetComponent<RenderedModelComponent>(id);

                var model = renderedModel.Model;
                if (model is null)
                {
                    continue;
                }

                var info = mLoadedModelInfo[renderedModel.ID];
                foreach (var mesh in model.Submeshes)
                {
                    var pipeline = info.Pipelines[mesh.MaterialIndex];
                    renderer.RenderMesh(model.VertexBuffer, model.IndexBuffer, pipeline,
                                        mesh.IndexOffset, mesh.IndexCount, DeviceBufferIndexType.UInt32,
                                        mapped =>
                    {
                        mReflectionView!.MapStructure(mapped, nameof(ModelShader.u_PushConstants), new PushConstantData
                        {
                            Model = Matrix4x4.Transpose(transform * mesh.Transform), // see OnUpdate
                            BoneOffset = renderedModel.BoneOffset
                        });
                    });
                }
            }
        }

        #endregion
        #region Menus

        [ImGuiMenu("Scene Hierarchy")]
        private void SceneHierarchy(IEnumerable<ImGuiMenu> children)
        {
            var deletedEntities = new HashSet<ulong>();
            bool entityHovered = false;

            foreach (ulong id in mRegistry)
            {
                string tag = TryGetComponent(id, out TagComponent? component) ? component.Tag : $"<no tag:{id}>";

                ImGui.PushID($"entity-{id}");
                if (ImGui.Selectable(tag, mSelectedEntity == id))
                {
                    mSelectedEntity = id;
                }

                entityHovered |= ImGui.IsItemHovered();
                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("Delete entity"))
                    {
                        deletedEntities.Add(id);
                        mSelectedEntity = Registry.Null;
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            foreach (ulong id in deletedEntities)
            {
                mRegistry.Destroy(id);
            }

            const string windowContextId = "window-context";
            if (ImGui.IsWindowHovered())
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    mSelectedEntity = Registry.Null;
                }

                // these bindings don't have right-click BeginPopupContextWindow
                if (ImGui.IsMouseDown(ImGuiMouseButton.Right) && !entityHovered)
                {
                    ImGui.OpenPopup(windowContextId);
                }
            }

            if (ImGui.BeginPopup(windowContextId))
            {
                if (ImGui.MenuItem("New entity"))
                {
                    mSelectedEntity = NewEntity();
                }

                ImGui.EndPopup();
            }
        }

        [ImGuiMenu("Editor")]
        private void Editor(IEnumerable<ImGuiMenu> children)
        {
            if (mSelectedEntity == Registry.Null)
            {
                ImGui.Text("No entity selected");
                return;
            }

            const string addComponentId = "add-component";
            if (ImGui.Button("Add component"))
            {
                ImGui.OpenPopup(addComponentId);
            }

            if (ImGui.BeginPopup(addComponentId))
            {
                foreach (var componentType in sComponentTypes)
                {
                    if (ImGui.MenuItem(componentType.DisplayName) && !mRegistry.Has(mSelectedEntity, componentType.Type))
                    {
                        AddComponent(mSelectedEntity, componentType.Type);
                    }
                }

                ImGui.EndPopup();
            }

            var style = ImGui.GetStyle();
            var font = ImGui.GetFont();

            var removedComponents = new List<Type>();
            foreach (var component in ViewComponents(mSelectedEntity))
            {
                var type = component.GetType();
                var fullName = type.FullName ?? type.Name;
                ImGui.PushID(fullName.Replace('.', '-'));

                var attribute = type.GetCustomAttribute<RegisteredComponentAttribute>();
                var displayName = attribute?.DisplayName;

                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = type.Name;
                }

                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f));
                float lineHeight = font.FontSize + style.FramePadding.Y * 2f;

                var regionAvailable = ImGui.GetContentRegionAvail();
                float xOffset = regionAvailable.X - lineHeight / 2f;

                bool open = ImGui.TreeNodeEx(displayName, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed |
                                                          ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowItemOverlap |
                                                          ImGuiTreeNodeFlags.FramePadding);

                ImGui.PopStyleVar();
                ImGui.SameLine(xOffset);

                const string componentSettingsId = "component-settings";
                if (ImGui.Button("+", new Vector2(lineHeight)))
                {
                    ImGui.OpenPopup(componentSettingsId);
                }

                if (ImGui.BeginPopup(componentSettingsId))
                {
                    if (ImGui.MenuItem("Remove"))
                    {
                        removedComponents.Add(type);
                    }

                    ImGui.EndPopup();
                }

                if (open)
                {
                    if (!InvokeComponentEvent(component, mSelectedEntity, "OnEdit"))
                    {
                        ImGui.Text("This component is not editable");
                    }

                    ImGui.TreePop();
                }

                ImGui.PopID();
            }

            foreach (var type in removedComponents)
            {
                RemoveComponent(mSelectedEntity, type);
            }
        }

        [ImGuiMenu("Model registry")]
        private unsafe void ModelRegistry(IEnumerable<ImGuiMenu> children)
        {
            var registry = App.Instance.ModelRegistry;
            if (registry is null)
            {
                ImGui.Text("The model registry has not been initialized!");
                return;
            }

            string hint = Path.GetFileNameWithoutExtension(mModelPath) ?? string.Empty;
            ImGui.InputText("Model path", ref mModelPath, 256);
            ImGui.InputTextWithHint("Model name", hint, ref mModelName, 256);

            if (ImGui.Button("Load model"))
            {
                if (string.IsNullOrEmpty(mModelPath))
                {
                    mModelError = "No path provided!";
                }
                else
                {
                    try
                    {
                        string name = string.IsNullOrEmpty(mModelName) ? hint : mModelName;
                        int modelId = registry.Load<ModelShader>(mModelPath, name, nameof(ModelShader.u_BoneBuffer));

                        if (modelId < 0)
                        {
                            mModelError = "Failed to load model!";
                        }
                        else
                        {
                            mModelName = mModelPath = mModelError = string.Empty;

                            var app = App.Instance;
                            var library = App.Instance.Renderer!.Library;
                            var renderTarget = app.GraphicsContext!.Swapchain.RenderTarget; // todo: framebuffer

                            var modelData = registry.Models[modelId];
                            var materials = modelData.Model.Materials;

                            var pipelines = new IPipeline[materials.Count];
                            for (int i = 0; i < materials.Count; i++)
                            {
                                var material = materials[i];
                                var pipeline = library.LoadPipeline<ModelShader>(new PipelineDescription
                                {
                                    RenderTarget = renderTarget,
                                    Type = PipelineType.Graphics,
                                    FrameCount = Renderer.FrameCount,
                                    Specification = material.PipelineSpecification
                                });

                                pipeline.Bind(mCameraBuffer!, nameof(ModelShader.u_CameraBuffer));
                                pipeline.Bind(modelData.BoneBuffer, nameof(ModelShader.u_BoneBuffer));
                                material.Bind(pipeline, nameof(ModelShader.u_MaterialBuffer), textureType => $"u_{textureType}Map");
                            }

                            mLoadedModelInfo.Add(modelId, new LoadedModelInfo
                            {
                                Pipelines = pipelines
                            });
                        }
                    }
                    catch (Exception exc)
                    {
                        var type = exc.GetType();
                        mModelError = $"Exception thrown ({type.FullName ?? type.Name}): {exc.Message}";
                    }
                }
            }

            if (!string.IsNullOrEmpty(mModelError))
            {
                ImGui.TextColored(new Vector4(0.8f, 0f, 0f, 1f), mModelError);
            }

            ImGui.Separator();
            var models = registry.Models;
            foreach (var id in models.Keys)
            {
                ImGui.PushID($"model-{id}");

                var name = registry.GetFormattedName(id);
                ImGui.Selectable(name);

                if (ImGui.BeginDragDropSource())
                {
                    ImGui.Text(name);
                    ImGui.SetDragDropPayload(RenderedModelComponent.ModelDragDropID, (nint)(void*)&id, sizeof(int));
                    ImGui.EndDragDropSource();
                }

                ImGui.PopID();
            }
        }

        #endregion
        #region ECS

        private bool InvokeComponentEvent(object component, ulong id, string name)
        {
            var type = component.GetType();
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, new Type[]
            {
                typeof(ulong),
                typeof(SceneLayer)
            });

            if (method is null)
            {
                return false;
            }

            method.Invoke(component, new object[]
            {
                id,
                this
            });

            return true;
        }

        public IDisposable LockRegistry() => mRegistry.Lock();
        public ulong NewEntity(string tag = "Entity")
        {
            ulong id = mRegistry.New();
            AddComponent<TagComponent>(id, tag);

            return id;
        }

        public void DestroyEntity(ulong id)
        {
            var types = ViewComponents(id).Select(component => component.GetType());
            foreach (var type in types)
            {
                RemoveComponent(id, type);
            }

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
            var component = mRegistry.Add(id, type, args);

            using var registryLock = LockRegistry();
            InvokeComponentEvent(component, id, "OnComponentAdded");

            return component;
        }

        public void RemoveComponent<T>(ulong id) where T : class => RemoveComponent(id, typeof(T));
        public void RemoveComponent(ulong id, Type type)
        {
            if (!TryGetComponent(id, type, out object? component))
            {
                return;
            }

            using (var registryLock = LockRegistry())
            {
                InvokeComponentEvent(component, id, "OnComponentRemoved");
            }

            mRegistry.Remove(id, type);
        }

        #endregion

        private ulong mSelectedEntity;
        private readonly Registry mRegistry;
        private readonly IReadOnlyList<ImGuiMenu> mMenus;
        private string mModelPath, mModelName, mModelError;
        private readonly Dictionary<int, LoadedModelInfo> mLoadedModelInfo;
        private IDeviceBuffer? mCameraBuffer;
        private IReflectionView? mReflectionView;
    }
}