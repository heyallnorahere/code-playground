using ImGuiNET;
using Ragdoll.Components;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;

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

        public override void OnUpdate(double delta)
        {
            // todo: update scene
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
            // todo: render scene
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
    }
}