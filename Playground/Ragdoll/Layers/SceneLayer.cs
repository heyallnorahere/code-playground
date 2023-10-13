using CodePlayground.Graphics;
using ImGuiNET;
using Optick.NET;
using Ragdoll.Components;
using Ragdoll.Physics;
using Ragdoll.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
            Fullscreen = false;
            RenderChildren = true;
            NoPadding = false;
        }

        public string Path { get; }
        public ImGuiWindowFlags Flags { get; set; }
        public bool Visible { get; set; }
        public bool Fullscreen { get; set; }
        public bool RenderChildren { get; set; }
        public bool NoPadding { get; set; }
    }

    internal delegate void ImGuiMenuCallback(IEnumerable<ImGuiMenu> children);
    internal sealed class ImGuiMenu
    {
        public ImGuiMenu(string title, ImGuiWindowFlags flags, bool visible, bool fullscreen, bool renderChildren, bool noPadding, ImGuiMenuCallback callback)
        {
            mVisible = visible;
            mFullscreen = fullscreen;
            mRenderChildren = renderChildren;
            mNoPadding = noPadding;
            mTitle = title;
            mFlags = flags;
            mCallback = callback;
            mChildren = new List<ImGuiMenu>();
        }

        public void Render(bool enableVisibility = true)
        {
            using var renderEvent = OptickMacros.Event();
            if (!mVisible)
            {
                return;
            }

            var flags = mFlags;
            if (mFullscreen)
            {
                var viewport = ImGui.GetMainViewport();

                ImGui.SetNextWindowPos(viewport.WorkPos);
                ImGui.SetNextWindowSize(viewport.WorkSize);
                ImGui.SetNextWindowViewport(viewport.ID);

                ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

                flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize;
                flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;
            }

            if (mNoPadding)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            }

            bool visible;
            if (enableVisibility)
            {
                visible = ImGui.Begin(mTitle, ref mVisible, flags);
            }
            else
            {
                visible = ImGui.Begin(mTitle, flags);
            }

            int styleVarCount = 0;
            if (mNoPadding)
            {
                styleVarCount++;
            }

            if (mFullscreen)
            {
                styleVarCount += 2;
            }

            if (styleVarCount > 0)
            {
                ImGui.PopStyleVar(styleVarCount);
            }

            if (visible)
            {
                mCallback.Invoke(mChildren);
            }

            ImGui.End();
            if (mRenderChildren && visible)
            {
                foreach (var child in mChildren)
                {
                    child.Render();
                }
            }
        }

        public ref bool Visible => ref mVisible;
        public ref bool Fullscreen => ref mFullscreen;
        public string Title => mTitle;
        public IList<ImGuiMenu> Children => mChildren;

        private bool mVisible, mFullscreen;
        private readonly bool mRenderChildren, mNoPadding;
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
        public BufferMatrix Projection, View;
        public Vector3 Position;
    }

    internal struct PushConstantData
    {
        public BufferMatrix Model;
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
            mSelectedEntity = Scene.Null;
            mMenus = LoadMenus();

            mModelPath = mModelName = mModelError = string.Empty;
            mLoadedModelInfo = new Dictionary<int, LoadedModelInfo>();

            mFramebufferAttachments = new FramebufferAttachmentInfo[2];

            mCameraBuffer = null;
            mReflectionView = null;

            mAttached = false;
            App.Instance.OptickStateChanged += OnOptickStateChanged;
        }

        private void OnOptickStateChanged(State state)
        {
            if (state != State.STOP_CAPTURE || !mAttached)
            {
                return;
            }

            AttachOptickScreenshots();
        }

        private int LoadModel(string path, string name)
        {
            using var loadModelEvent = OptickMacros.Event(category: Category.IO);

            var app = App.Instance;
            var library = app.Renderer!.Library;
            var registry = app.ModelRegistry!;

            int modelId = registry.Load<ModelShader>(path, name, nameof(ModelShader.u_BoneBuffer));
            if (modelId < 0)
            {
                return -1;
            }

            var modelData = registry.Models[modelId];
            var materials = modelData.Model.Materials;

            var pipelines = new IPipeline[materials.Count];
            for (int i = 0; i < materials.Count; i++)
            {
                var material = materials[i];
                var pipeline = pipelines[i] = library.LoadPipeline<ModelShader>(new PipelineDescription
                {
                    RenderTarget = mRenderTarget,
                    Type = PipelineType.Graphics,
                    FrameCount = Renderer.FrameCount,
                    Specification = material.PipelineSpecification
                });

                pipeline.Bind(mCameraBuffer!, nameof(ModelShader.u_CameraBuffer));
                pipeline.Bind(mLightBuffer!, nameof(ModelShader.u_LightBuffer));
                pipeline.Bind(modelData.BoneBuffer, nameof(ModelShader.u_BoneBuffer));
                material.Bind(pipeline, nameof(ModelShader.u_MaterialBuffer), textureType => $"u_{textureType}Map");
            }

            mLoadedModelInfo.Add(modelId, new LoadedModelInfo
            {
                Pipelines = pipelines
            });

            return modelId;
        }

        #region Menu loading

        private IReadOnlyList<ImGuiMenu> LoadMenus()
        {
            using var loadMenusEvent = OptickMacros.Event();

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
                var menu = new ImGuiMenu(path[^1], attribute.Flags, attribute.Visible, attribute.Fullscreen, attribute.RenderChildren, attribute.NoPadding, callback);

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
            using var pushedEvent = OptickMacros.Event();

            var app = App.Instance;
            var graphicsContext = app.GraphicsContext!;

            // for transfer operations
            var queue = graphicsContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();
            commandList.Begin();

            using (commandList.Context(GPUQueueType.Transfer))
            {
                mReflectionView = app.Renderer!.Library.CreateReflectionView<ModelShader>();
                int cameraBufferSize = mReflectionView.GetBufferSize(nameof(ModelShader.u_CameraBuffer));
                if (cameraBufferSize < 0)
                {
                    throw new InvalidOperationException("Failed to find camera buffer!");
                }

                int lightBufferSize = mReflectionView.GetBufferSize(nameof(ModelShader.u_LightBuffer));
                if (lightBufferSize < 0)
                {
                    throw new InvalidOperationException("Failed to find light buffer!");
                }

                mCameraBuffer = graphicsContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, cameraBufferSize);
                mLightBuffer = graphicsContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, lightBufferSize);

                mFramebufferSemaphore = graphicsContext.CreateSemaphore();
                mFramebufferRecreated = true;

                var size = app.RootWindow!.FramebufferSize;
                CreateFramebufferAttachments(size.X, size.Y, commandList);
                mFramebuffer = graphicsContext.CreateFramebuffer(new FramebufferInfo
                {
                    Width = size.X,
                    Height = size.Y,
                    Attachments = mFramebufferAttachments
                }, out mRenderTarget);
            }

            commandList.AddSemaphore(mFramebufferSemaphore, SemaphoreUsage.Signal);
            commandList.End();
            queue.Submit(commandList);

            // test scene
            mScene = new Scene
            {
                UpdatePhysics = false
            };

            using (OptickMacros.Event("Test scene creation"))
            {
                int modelId = LoadModel("../../../../VulkanTest/Resources/Models/sledge-hammer.fbx", "sledge-hammer");
                ulong entity = mScene.NewEntity("Model");
                mScene.AddComponent<RenderedModelComponent>(entity).UpdateModel(modelId, entity);

                var transform = mScene.AddComponent<TransformComponent>(entity);
                transform.Scale = Vector3.One * 10f;
                transform.RotationEuler = new Vector3(45f, 0f, 0f);

                var rigidBody = mScene.AddComponent<RigidBodyComponent>(entity);
                var staticModelCollider = (StaticModelCollider)rigidBody.CreateCollider(ColliderType.StaticModel);
                staticModelCollider.SetModel(modelId);

                entity = mScene.NewEntity("Camera");
                mScene.AddComponent<CameraComponent>(entity).MainCamera = true;

                transform = mScene.AddComponent<TransformComponent>(entity);
                transform.Translation = (Vector3.UnitY - Vector3.UnitZ) * 7.5f;
                transform.RotationEuler = Vector3.UnitX * MathF.PI / 4f;

                modelId = LoadModel("../../../../VulkanTest/Resources/Models/cube.obj", "cube");
                entity = mScene.NewEntity("Floor");

                transform = mScene.AddComponent<TransformComponent>(entity);
                transform.Translation.Y = -5f;
                transform.Scale = (Vector3.One - Vector3.UnitY) * 10f + Vector3.UnitY;

                mScene.AddComponent<RenderedModelComponent>(entity).UpdateModel(modelId, entity);
                mScene.AddComponent<RigidBodyComponent>(entity).BodyType = BodyType.Static;

                entity = mScene.NewEntity("Light");
                mScene.AddComponent<TransformComponent>(entity).Translation = Vector3.UnitY * 1.5f;

                var light = mScene.AddComponent<LightComponent>(entity);
                light.DiffuseStrength = 1f;
                light.SpecularStrength = 0.5f;
                light.AmbientStrength = 0.1f;
            }

            mAttached = true;
        }

        private void CreateFramebufferAttachments(int width, int height, ICommandList commandList)
        {
            using var createEvent = OptickMacros.Event();

            var graphicsContext = App.Instance.GraphicsContext!;
            var colorImage = graphicsContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(width, height),
                Usage = DeviceImageUsageFlags.Render | DeviceImageUsageFlags.ColorAttachment | DeviceImageUsageFlags.CopySource,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            });

            var depthImage = graphicsContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(width, height),
                Usage = DeviceImageUsageFlags.DepthStencilAttachment,
                Format = DeviceImageFormat.DepthStencil,
                MipLevels = 1
            });

            var colorLayout = colorImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            colorImage.TransitionLayout(commandList, colorImage.Layout, colorLayout);
            colorImage.Layout = colorLayout;

            var depthLayout = depthImage.GetLayout(DeviceImageLayoutName.DepthStencilAttachment);
            depthImage.TransitionLayout(commandList, depthImage.Layout, depthLayout);
            depthImage.Layout = depthLayout;

            mFramebufferAttachments[0] = new FramebufferAttachmentInfo
            {
                Image = colorImage,
                Type = AttachmentType.Color,
                Layout = colorImage.GetLayout(DeviceImageLayoutName.ColorAttachment)
            };

            mFramebufferAttachments[1] = new FramebufferAttachmentInfo
            {
                Image = depthImage,
                Type = AttachmentType.DepthStencil
            };

            mViewportTexture = colorImage.CreateTexture(false, null);
        }

        private void DestroyFramebuffer()
        {
            using var destroyEvent = OptickMacros.Event();

            mFramebuffer?.Dispose();
            mViewportTexture?.Dispose();

            foreach (var attachment in mFramebufferAttachments)
            {
                attachment.Image.Dispose();
            }
        }

        public override void OnPopped()
        {
            using var poppedEvent = OptickMacros.Event();
            mAttached = false;

            mCameraBuffer?.Dispose();
            mLightBuffer?.Dispose();

            foreach (var modelData in mLoadedModelInfo.Values)
            {
                foreach (var pipeline in modelData.Pipelines)
                {
                    pipeline.Dispose();
                }
            }

            AttachOptickScreenshots();
            DestroyFramebuffer();

            mRenderTarget?.Dispose();
            mFramebufferSemaphore?.Dispose();

            mScene?.Dispose();
            mScene = null;
            mSelectedEntity = Scene.Null;
        }

        private void AttachOptickScreenshots()
        {
            using var attachScreenshotsEvent = OptickMacros.Event();

            // currently only need one
            CreateOptickScreenshot(mFramebufferAttachments[0].Image, "viewport.png");
        }

        private unsafe void CreateOptickScreenshot(IDeviceImage image, string filename)
        {
            using var createScreenshotEvent = OptickMacros.Event();

            var context = App.Instance.GraphicsContext!;
            var device = context.Device;
            var queue = device.GetQueue(CommandQueueFlags.Transfer);

            var commandList = queue.Release();
            commandList.Begin();

            int width = image.Size.Width;
            int height = image.Size.Height;
            int bufferSize = width * height * 4; // RGBA8_UNORM

            var stagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);
            commandList.PushStagingObject(stagingBuffer);

            using (commandList.Context(GPUQueueType.Transfer))
            {
                image.CopyToBuffer(commandList, stagingBuffer, image.Layout);
            }

            commandList.End();
            queue.Submit(commandList, true);

            var imageBuffer = new byte[bufferSize];
            stagingBuffer.CopyToCPU(imageBuffer);

            var loadedImage = Image.LoadPixelData<Rgba32>(imageBuffer, width, height);
            using var stream = new MemoryStream();
            loadedImage.SaveAsPng(stream);

            stream.Position = 0;
            var fileBuffer = new byte[stream.Length];

            while (true)
            {
                int position = (int)stream.Position;
                int count = stream.Read(fileBuffer, position, fileBuffer.Length - position);

                if (count <= 0)
                {
                    break;
                }
            }

            fixed (void* fileData = fileBuffer)
            {
                OptickImports.AttachFile(FileType.OPTICK_IMAGE, filename, fileData, (uint)fileBuffer.Length);
            }
        }

        private static void ComputeCameraVectors(Vector3 angle, out Vector3 direction, out Vector3 up)
        {
            using var computeCameraVectorsEvent = OptickMacros.Event();

            // +X - tilt camera up
            // +Y - rotate view to the right
            // +Z - roll camera counterclockwise

            float pitch = -angle.X;
            float yaw = -angle.Y;
            float roll = angle.Z + MathF.PI / 2f; // we want the up vector to face +Y at 0 degrees roll

            // yaw offset of 90 degrees - we want the camera to be facing +Z at 0 degrees yaw
            float directionPitch = MathF.Sin(roll) * pitch - MathF.Cos(roll) * yaw;
            float directionYaw = MathF.Sin(roll) * yaw - MathF.Cos(roll) * pitch + MathF.PI / 2f;

            direction = Vector3.Normalize(new Vector3
            {
                X = MathF.Cos(directionYaw) * MathF.Cos(directionPitch),
                Y = MathF.Sin(directionPitch),
                Z = MathF.Sin(directionYaw) * MathF.Cos(directionPitch)
            });

            up = Vector3.Normalize(new Vector3
            {
                X = MathF.Cos(yaw) * MathF.Cos(roll),
                Y = MathF.Sin(roll),
                Z = MathF.Sin(yaw) * MathF.Cos(roll)
            });
        }

        public override void OnUpdate(double delta)
        {
            if (mScene is null)
            {
                return;
            }

            using var updateEvent = OptickMacros.Event(category: Category.GameLogic);
            mScene.Update(delta);

            var app = App.Instance;
            var context = app.GraphicsContext;
            var registry = app.ModelRegistry;

            if (context is not null)
            {
                if (registry is not null)
                {
                    using var boneUpdateEvent = OptickMacros.Category("Bone update", Category.Animation);
                    using (OptickMacros.Event("Pre-bone update"))
                    {
                        var entityView = mScene.ViewEntities(typeof(BoneControllerComponent), typeof(TransformComponent));
                        foreach (var entity in entityView)
                        {
                            var boneControllerComponent = mScene.GetComponent<BoneControllerComponent>(entity);
                            mScene.InvokeComponentEvent(boneControllerComponent, entity, ComponentEventID.PreBoneUpdate);
                        }
                    }

                    var math = new MatrixMath(context);
                    foreach (ulong entity in mScene.ViewEntities(typeof(RenderedModelComponent)))
                    {
                        var modelData = mScene.GetComponent<RenderedModelComponent>(entity);
                        if (modelData.ID < 0)
                        {
                            continue;
                        }

                        var boneBuffer = registry.Models[modelData.ID].BoneBuffer;
                        modelData.BoneController?.Update(boneTransforms => boneBuffer.CopyFromCPU(boneTransforms.Select(math.TranslateMatrix).ToArray(), modelData.BoneOffset * Marshal.SizeOf<BufferMatrix>()));
                    }
                }

                using var updateMatricesEvent = OptickMacros.Category("Update scene matrices", Category.Rendering);

                var camera = Scene.Null;
                foreach (ulong entity in mScene.ViewEntities(typeof(TransformComponent), typeof(CameraComponent)))
                {
                    var component = mScene.GetComponent<CameraComponent>(entity);
                    if (component.MainCamera)
                    {
                        camera = entity;
                        break;
                    }
                }

                if (camera != Scene.Null)
                {
                    var transform = mScene.GetComponent<TransformComponent>(camera);
                    var cameraData = mScene.GetComponent<CameraComponent>(camera);

                    var math = new MatrixMath(context);
                    float aspectRatio = (float)mFramebuffer!.Width / mFramebuffer!.Height;
                    float fov = cameraData.FOV * MathF.PI / 180f;
                    var projection = math.Perspective(fov, aspectRatio, 0.1f, 100f);

                    ComputeCameraVectors(transform.RotationEuler, out Vector3 direction, out Vector3 up);
                    var view = math.LookAt(transform.Translation, transform.Translation + direction, up);

                    mCameraBuffer?.MapStructure(mReflectionView!, nameof(ModelShader.u_CameraBuffer), new CameraBufferData
                    {
                        Projection = math.TranslateMatrix(projection),
                        View = math.TranslateMatrix(view),
                        Position = transform.Translation
                    });
                }

                mLightBuffer?.Map(data =>
                {
                    if (mReflectionView is null)
                    {
                        return;
                    }

                    var bufferNode = mReflectionView.GetResourceNode(nameof(ModelShader.u_LightBuffer));
                    if (bufferNode is null)
                    {
                        return;
                    }

                    int currentPointLightIndex = 0;
                    foreach (ulong entity in mScene.ViewEntities(typeof(LightComponent)))
                    {
                        var light = mScene.GetComponent<LightComponent>(entity);

                        Vector3 position;
                        // no conditional - only point light is defined
                        if (!mScene.TryGetComponent(entity, out TransformComponent? transform))
                        {
                            continue;
                        }

                        var transformMatrix = Matrix4x4.Transpose(transform.CreateMatrix(TransformComponents.NonDeformative));
                        position = Vector3.Transform(light.PositionOffset, transformMatrix);

                        switch (light.Type)
                        {
                            case LightType.Point:
                                {
                                    var pointLightNode = bufferNode.Find($"{nameof(ModelShader.LightBufferData.PointLights)}[{currentPointLightIndex++}]");
                                    if (pointLightNode is null)
                                    {
                                        continue;
                                    }

                                    pointLightNode.Set(data, nameof(ModelShader.PointLightData.Diffuse), light.DiffuseColor * light.DiffuseStrength);
                                    pointLightNode.Set(data, nameof(ModelShader.PointLightData.Specular), light.SpecularColor * light.SpecularStrength);
                                    pointLightNode.Set(data, nameof(ModelShader.PointLightData.Ambient), light.AmbientColor * light.AmbientStrength);
                                    pointLightNode.Set(data, nameof(ModelShader.PointLightData.Position), position);

                                    var attenuationNode = pointLightNode.Find(nameof(ModelShader.PointLightData.Attenuation));
                                    if (attenuationNode is null)
                                    {
                                        continue;
                                    }

                                    attenuationNode.Set(data, nameof(ModelShader.AttenuationData.Quadratic), light.Quadratic);
                                    attenuationNode.Set(data, nameof(ModelShader.AttenuationData.Linear), light.Linear);
                                    attenuationNode.Set(data, nameof(ModelShader.AttenuationData.Constant), light.Constant);
                                }
                                break;
                        }
                    }

                    bufferNode.Set(data, nameof(ModelShader.LightBufferData.PointLightCount), currentPointLightIndex);
                });
            }
        }

        public override void OnImGuiRender()
        {
            foreach (var menu in mMenus)
            {
                menu.Render(false);
            }
        }

        public override void PreRender(Renderer renderer)
        {
            using var preRenderEvent = OptickMacros.Event(category: Category.Rendering);
            if (mScene is null)
            {
                return;
            }

            var commandList = renderer.FrameInfo.CommandList;
            if (mFramebufferRecreated)
            {
                commandList.AddSemaphore(mFramebufferSemaphore!, SemaphoreUsage.Wait);
                mFramebufferRecreated = false;
            }

            var colorAttachment = mFramebufferAttachments[0];
            var attachmentLayout = colorAttachment.Layout!;
            var renderLayout = colorAttachment.Image.Layout;

            colorAttachment.Image.TransitionLayout(commandList, renderLayout, attachmentLayout);
            renderer.BeginRender(mRenderTarget!, mFramebuffer!, new Vector4(0.2f, 0.2f, 0.2f, 1f));

            var math = new MatrixMath(renderer.Context);
            foreach (ulong id in mScene.ViewEntities(typeof(TransformComponent), typeof(RenderedModelComponent)))
            {
                var transform = mScene.GetComponent<TransformComponent>(id);
                var renderedModel = mScene.GetComponent<RenderedModelComponent>(id);

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
                        using var pushConstantsEvent = OptickMacros.Category("Push constants", Category.Rendering);

                        mReflectionView!.MapStructure(mapped, nameof(ModelShader.u_PushConstants), new PushConstantData
                        {
                            Model = math.TranslateMatrix(transform.CreateMatrix()),
                            BoneOffset = renderedModel.BoneOffset
                        });
                    });
                }
            }

            renderer.EndRender();
            colorAttachment.Image.TransitionLayout(commandList, attachmentLayout, renderLayout);
        }

        #endregion
        #region Menus

        private void RecreateFramebuffer(int width, int height)
        {
            using var recreateEvent = OptickMacros.Event();
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var graphicsContext = App.Instance.GraphicsContext!;
            var device = graphicsContext.Device;
            device.ClearQueues();

            var queue = device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();
            commandList.Begin();

            using (commandList.Context(GPUQueueType.Transfer))
            {
                DestroyFramebuffer();
                CreateFramebufferAttachments(width, height, commandList);

                mFramebuffer = graphicsContext.CreateFramebuffer(new FramebufferInfo
                {
                    Width = width,
                    Height = height,
                    Attachments = mFramebufferAttachments
                }, mRenderTarget!);

                if (!mFramebufferRecreated)
                {
                    mFramebufferRecreated = true;
                    commandList.AddSemaphore(mFramebufferSemaphore!, SemaphoreUsage.Signal);
                }
            }

            commandList.End();
            queue.Submit(commandList);
        }

        [ImGuiMenu("Dockspace/Viewport", NoPadding = true)]
        private void Viewport(IEnumerable<ImGuiMenu> children)
        {
            using var viewportEvent = OptickMacros.Event();
            var imageSize = ImGui.GetContentRegionAvail();

            int width = (int)imageSize.X;
            int height = (int)imageSize.Y;
            if (width != mFramebuffer?.Width || height != mFramebuffer?.Height)
            {
                RecreateFramebuffer(width, height);
            }

            var app = App.Instance;
            var imguiLayer = app.LayerView.FindLayer<ImGuiLayer>();
            if (imguiLayer is null)
            {
                throw new InvalidOperationException("Failed to find ImGui layer!");
            }

            nint id = imguiLayer.Controller.GetTextureID(mViewportTexture!);
            ImGui.Image(id, imageSize);
        }

        [ImGuiMenu("Dockspace/Scene")]
        private unsafe void SceneMenu(IEnumerable<ImGuiMenu> children)
        {
            using var sceneMenuEvent = OptickMacros.Event();
            if (mScene is null)
            {
                ImGui.Text("No scene is associated with the scene layer");
                return;
            }

            var io = ImGui.GetIO();
            ImGui.Text($"FPS: {io.Framerate:0.###}");

            if (ImGui.Button(mScene.UpdatePhysics ? "Pause" : "Resume"))
            {
                mScene.UpdatePhysics = !mScene.UpdatePhysics;
            }

            ImGui.SameLine();
            ImGui.Text("Physics simulation");

            ImGui.DragFloat3("Gravity", ref mScene.Gravity, 0.1f);
            ImGui.DragFloat("Linear velocity damping", ref mScene.VelocityDamping.Linear, 0.01f);
            ImGui.DragFloat("Angular velocity damping", ref mScene.VelocityDamping.Angular, 0.01f);
            ImGui.Separator();

            var deletedEntities = new HashSet<ulong>();
            bool entityHovered = false;

            using (OptickMacros.Event("Entity list"))
            {
                foreach (ulong id in mScene.Entities)
                {
                    string tag = mScene.GetDisplayedEntityTag(id);

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
                            mSelectedEntity = Scene.Null;
                        }

                        ImGui.EndPopup();
                    }

                    if (ImGui.BeginDragDropSource())
                    {
                        ImGui.Text(tag);
                        ImGui.SetDragDropPayload(Scene.EntityDragDropID, (nint)(void*)&id, sizeof(ulong));
                        ImGui.EndDragDropSource();
                    }

                    ImGui.PopID();
                }
            }

            foreach (ulong id in deletedEntities)
            {
                mScene.DestroyEntity(id);
            }

            const string windowContextId = "window-context";
            if (ImGui.IsWindowHovered())
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    mSelectedEntity = Scene.Null;
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
                    mSelectedEntity = mScene.NewEntity();
                }

                ImGui.EndPopup();
            }
        }

        [ImGuiMenu("Dockspace/Editor")]
        private void Editor(IEnumerable<ImGuiMenu> children)
        {
            using var editorEvent = OptickMacros.Event();
            if (mSelectedEntity == Scene.Null || mScene is null)
            {
                ImGui.Text("No entity selected");
                return;
            }

            const string addComponentId = "add-component";
            if (ImGui.Button("Add component"))
            {
                ImGui.OpenPopup(addComponentId);
            }

            using (OptickMacros.Event("Add component"))
            {
                if (ImGui.BeginPopup(addComponentId))
                {
                    foreach (var componentType in sComponentTypes)
                    {
                        if (ImGui.MenuItem(componentType.DisplayName) && !mScene.HasComponent(mSelectedEntity, componentType.Type))
                        {
                            mScene.AddComponent(mSelectedEntity, componentType.Type);
                        }
                    }

                    ImGui.EndPopup();
                }
            }

            var removedComponents = new List<Type>();
            using (OptickMacros.Event("Component list"))
            {
                var style = ImGui.GetStyle();
                var font = ImGui.GetFont();

                foreach (var component in mScene.ViewComponents(mSelectedEntity))
                {
                    using var componentEvent = OptickMacros.Event("Edited component");

                    var type = component.GetType();
                    var fullName = type.FullName ?? type.Name;
                    OptickMacros.Tag("Component type", fullName);

                    var componentTypeId = fullName.Replace('.', '-');
                    ImGui.PushID($"{componentTypeId}-header");

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
                        ImGui.PushID(componentTypeId);
                        if (!mScene.InvokeComponentEvent(component, mSelectedEntity, ComponentEventID.Edited))
                        {
                            ImGui.Text("This component is not editable");
                        }

                        ImGui.PopID();
                        ImGui.TreePop();
                    }

                    ImGui.PopID();
                }
            }

            using (OptickMacros.Event("Removing components"))
            {
                foreach (var type in removedComponents)
                {
                    mScene.RemoveComponent(mSelectedEntity, type);
                }
            }
        }

        [ImGuiMenu("Dockspace/Model registry")]
        private unsafe void ModelRegistryMenu(IEnumerable<ImGuiMenu> children)
        {
            using var modelRegistryEvent = OptickMacros.Event();

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
                using var loadModelEvent = OptickMacros.Event();
                if (string.IsNullOrEmpty(mModelPath))
                {
                    mModelError = "No path provided!";
                }
                else
                {
                    try
                    {
                        string name = string.IsNullOrEmpty(mModelName) ? hint : mModelName;
                        if (LoadModel(mModelPath, name) < 0)
                        {
                            mModelError = "Failed to load model!";
                        }
                        else
                        {
                            mModelName = mModelPath = mModelError = string.Empty;
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
                    ImGui.SetDragDropPayload(ModelRegistry.RegisteredModelID, (nint)(void*)&id, sizeof(int));
                    ImGui.EndDragDropSource();
                }

                ImGui.PopID();
            }
        }

        [ImGuiMenu("Dockspace", Fullscreen = true, NoPadding = true, Flags = ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoDocking)]
        private void Dockspace(IEnumerable<ImGuiMenu> children)
        {
            using var dockspaceEvent = OptickMacros.Event();

            var io = ImGui.GetIO();
            if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.DockingEnable))
            {
                var id = ImGui.GetID("main-dockspace");
                ImGui.DockSpace(id, Vector2.Zero, ImGuiDockNodeFlags.None);
            }
            else
            {
                ImGui.Text("Docking has been disabled");
            }

            if (ImGui.BeginMenuBar())
            {
                if (ImGui.BeginMenu("View"))
                {
                    foreach (var child in children)
                    {
                        ImGui.MenuItem(child.Title, string.Empty, ref child.Visible);
                    }

                    ImGui.EndMenu();
                }

                ImGui.EndMenuBar();
            }
        }

        #endregion

        public Scene Scene => mScene!;

        private ulong mSelectedEntity;
        private Scene? mScene;

        private readonly IReadOnlyList<ImGuiMenu> mMenus;
        private string mModelPath, mModelName, mModelError;
        private readonly Dictionary<int, LoadedModelInfo> mLoadedModelInfo;

        private IFramebuffer? mFramebuffer;
        private IRenderTarget? mRenderTarget;
        private FramebufferAttachmentInfo[] mFramebufferAttachments;
        private ITexture? mViewportTexture;

        private IDisposable? mFramebufferSemaphore;
        private bool mFramebufferRecreated;

        private IDeviceBuffer? mCameraBuffer, mLightBuffer;
        private IReflectionView? mReflectionView;

        private bool mAttached;
    }
}