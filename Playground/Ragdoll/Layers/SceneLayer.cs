using ImGuiNET;
using Ragdoll.Components;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
                ulong id = mRegistry.New();
                mRegistry.Add<TagComponent>(id, $"Entity {i + 1}");
            }
        }

        #region Menu shit

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
                string tag = mRegistry.TryGet(id, out TagComponent? component) ? component.Tag : $"<no tag:{id}>";

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
                    mSelectedEntity = mRegistry.New();
                    mRegistry.Add<TagComponent>(mSelectedEntity);
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
                        mRegistry.Add(mSelectedEntity, componentType.Type);
                    }
                }

                ImGui.EndPopup();
            }

            var style = ImGui.GetStyle();
            var font = ImGui.GetFont();

            var removedComponents = new List<Type>();
            foreach (var component in mRegistry.View(mSelectedEntity))
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
                    var editMethod = type.GetMethod("Edit", BindingFlags.Public | BindingFlags.Instance, Array.Empty<Type>());
                    if (editMethod is not null)
                    {
                        editMethod.Invoke(component, null);
                    }
                    else
                    {
                        ImGui.Text("This component is not editable");
                    }

                    ImGui.TreePop();
                }

                ImGui.PopID();
            }

            foreach (var type in removedComponents)
            {
                mRegistry.Remove(mSelectedEntity, type);
            }
        }

        #endregion

        private ulong mSelectedEntity;
        private readonly Registry mRegistry;
        private readonly IReadOnlyList<ImGuiMenu> mMenus;
    }
}