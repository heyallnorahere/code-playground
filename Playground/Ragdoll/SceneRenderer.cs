using CodePlayground.Graphics;
using Optick.NET;
using Ragdoll.Components;
using Ragdoll.Shaders;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ragdoll
{
    internal struct LoadedModelInfo
    {
        public IPipeline[] RenderPipelines { get; set; }
        public IPipeline PointShadowMap { get; set; }
    }

    internal struct CameraBufferData
    {
        public BufferMatrix Projection, View;
        public Vector3 Position;
    }

    internal struct ShadowMapFramebuffer
    {
        public IFramebuffer Framebuffer;
        public IDeviceImage Attachment;
    }

    public struct SceneRenderInfo
    {
        public Renderer Renderer;
        public Scene Scene;
        public IFramebuffer Framebuffer;

        public Action BeginSceneRender, EndSceneRender;
    }

    internal enum RenderPassType
    {
        PointShadowMap,
        Render
    }

    internal delegate void PushConstantCallback(Span<byte> data, IReflectionNode reflectionNode);
    public sealed class SceneRenderer : IDisposable
    {
        public const int ShadowResolution = 1024;
        public const bool CheckpointsEnabled = false;

        public const float FarPlane = 100f;
        public const float NearPlane = 0.1f;

        private static readonly IReadOnlyDictionary<LightType, int> sLightCountLimits;
        static SceneRenderer()
        {
            sLightCountLimits = new Dictionary<LightType, int>
            {
                [LightType.Point] = ModelShader.MaxPointLights
            };
        }

        public SceneRenderer(IGraphicsContext context, ShaderLibrary library, ModelRegistry registry)
        {
            mDisposed = false;
            mContext = context;

            mShaderLibrary = library;
            mModelRegistry = registry;
            mMatrixMath = new MatrixMath(context);

            mLoadedModelInfo = new Dictionary<int, LoadedModelInfo>();
            mReflectionViews = new Dictionary<Type, IReflectionView>();
            mCheckpointStack = new CheckpointStack(CheckpointsEnabled);

            foreach (var type in library.CompiledTypes)
            {
                mReflectionViews.Add(type, library.CreateReflectionView(type));
            }

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            using (commandList.Context(GPUQueueType.Transfer))
            {
                CreateShadowMaps(commandList, out mShadowMaps);
                CreateShadowMapFramebuffer(commandList, out mShadowMapRenderTarget, out mShadowMapFramebuffers);
            }

            mInitializationSemaphore = context.CreateSemaphore();
            mWaitForInitialization = true;
            commandList.AddSemaphore(mInitializationSemaphore, SemaphoreUsage.Signal);

            commandList.End();
            queue.Submit(commandList);

            CreateBuffers(out mCameraBuffer, out mLightBuffer);
        }

        ~SceneRenderer()
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

        public void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            mCameraBuffer.Dispose();
            mLightBuffer.Dispose();

            foreach (var modelData in mLoadedModelInfo.Values)
            {
                foreach (var pipeline in modelData.RenderPipelines)
                {
                    pipeline.Dispose();
                }

                modelData.PointShadowMap.Dispose();
            }

            foreach (var shadowMaps in mShadowMaps.Values)
            {
                foreach (var shadowMap in shadowMaps)
                {
                    shadowMap.Dispose();
                }
            }

            foreach (var framebuffer in mShadowMapFramebuffers)
            {
                framebuffer.Framebuffer.Dispose();
                framebuffer.Attachment.Dispose();
            }

            mShadowMapRenderTarget.Dispose();
            mInitializationSemaphore.Dispose();
        }

        private void CreateShadowMaps(ICommandList commandList, out Dictionary<LightType, ITexture[]> shadowMaps)
        {
            using var createFramebuffersEvent = OptickMacros.Event();

            shadowMaps = new Dictionary<LightType, ITexture[]>();
            foreach ((var type, int limit) in sLightCountLimits)
            {
                var lightShadowMaps = new ITexture[limit];
                for (int i = 0; i < limit; i++)
                {
                    var image = mContext.CreateDeviceImage(new DeviceImageInfo
                    {
                        Size = new Size(ShadowResolution, ShadowResolution),
                        ImageType = DeviceImageType.TypeCube,
                        Usage = DeviceImageUsageFlags.Render | DeviceImageUsageFlags.CopyDestination,
                        Format = DeviceImageFormat.DepthStencil,
                        MipLevels = 1
                    });

                    var newLayout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
                    image.TransitionLayout(commandList, image.Layout, newLayout);
                    image.Layout = newLayout;

                    lightShadowMaps[i] = image.CreateTexture(true);
                }

                shadowMaps.Add(type, lightShadowMaps);
            }
        }

        private void CreateShadowMapFramebuffer(ICommandList commandList, out IRenderTarget renderTarget, out ShadowMapFramebuffer[] framebuffers)
        {
            IRenderTarget? framebufferRenderTarget = null;
            framebuffers = new ShadowMapFramebuffer[ModelShader.CubemapFaceCount];

            for (int i = 0; i < framebuffers.Length; i++)
            {
                var attachment = mContext.CreateDeviceImage(new DeviceImageInfo
                {
                    Size = new Size(ShadowResolution, ShadowResolution),
                    ImageType = DeviceImageType.Type2D,
                    Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.DepthStencilAttachment,
                    Format = DeviceImageFormat.DepthStencil,
                    MipLevels = 1
                });

                var layout = attachment.GetLayout(DeviceImageLayoutName.CopySource);
                attachment.TransitionLayout(commandList, attachment.Layout, layout);
                attachment.Layout = layout;

                layout = attachment.GetLayout(DeviceImageLayoutName.DepthStencilAttachment);
                var info = new FramebufferInfo
                {
                    Width = ShadowResolution,
                    Height = ShadowResolution,
                    Attachments = new FramebufferAttachmentInfo[]
                    {
                        new FramebufferAttachmentInfo
                        {
                            Image = attachment,
                            Type = AttachmentType.DepthStencil,
                            InitialLayout = layout,
                            FinalLayout = layout,
                            Layout = layout
                        }
                    }
                };

                IFramebuffer framebuffer;
                if (framebufferRenderTarget is null)
                {
                    framebuffer = mContext.CreateFramebuffer(info, out framebufferRenderTarget);
                }
                else
                {
                    framebuffer = mContext.CreateFramebuffer(info, framebufferRenderTarget);
                }

                framebuffers[i] = new ShadowMapFramebuffer
                {
                    Framebuffer = framebuffer,
                    Attachment = attachment
                };
            }

            renderTarget = framebufferRenderTarget!;
        }

        private void CreateBuffers(out IDeviceBuffer cameraBuffer, out IDeviceBuffer lightBuffer)
        {
            using var createBuffersEvent = OptickMacros.Event();
            var reflectionView = GetReflectionView<ModelShader>();

            int cameraBufferSize = reflectionView.GetBufferSize(nameof(ModelShader.u_CameraBuffer));
            if (cameraBufferSize < 0)
            {
                throw new InvalidOperationException("Failed to find camera buffer!");
            }

            int lightBufferSize = reflectionView.GetBufferSize(nameof(ModelShader.u_LightBuffer));
            if (lightBufferSize < 0)
            {
                throw new InvalidOperationException("Failed to find light buffer!");
            }

            cameraBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, cameraBufferSize);
            lightBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, lightBufferSize);
        }

        private IPipeline CreatePipeline<T>(IRenderTarget renderTarget, Material? material, IReadOnlyDictionary<string, IDeviceBuffer>? additionalBuffers = null) where T : class
        {
            var pipeline = mShaderLibrary.LoadPipeline<T>(new PipelineDescription
            {
                RenderTarget = renderTarget,
                Type = PipelineType.Graphics,
                FrameCount = Renderer.FrameCount,
                Specification = material?.PipelineSpecification ?? Material.CreateDefaultPipelineSpec(),
                VertexAttributeLayout = GetReflectionView<ModelShader>().CreateVertexAttributeLayout()
            });

            var boundBuffers = new Dictionary<string, IDeviceBuffer>
            {
                [nameof(ModelShader.u_CameraBuffer)] = mCameraBuffer,
                [nameof(ModelShader.u_LightBuffer)] = mLightBuffer
            };

            if (additionalBuffers is not null)
            {
                foreach ((var name, var buffer) in additionalBuffers)
                {
                    boundBuffers.TryAdd(name, buffer);
                }
            }

            foreach ((var name, var buffer) in boundBuffers)
            {
                pipeline.Bind(buffer, ShaderLibrary.ConvertResourceName<T, ModelShader>(name));
            }

            var materialBufferName = ShaderLibrary.ConvertResourceName<T, ModelShader>(nameof(ModelShader.u_MaterialBuffer));
            material?.Bind(pipeline, materialBufferName, textureType => ShaderLibrary.ConvertResourceName<T, ModelShader>($"u_{textureType}Map"));

            return pipeline;
        }

        public void RegisterModel(int model, IRenderTarget renderTarget)
        {
            using var registerModelEvent = OptickMacros.Event();

            var modelData = mModelRegistry.Models[model];
            var materials = modelData.Model.Materials;

            var renderPipelines = new IPipeline[materials.Count];
            var boundBuffers = new Dictionary<string, IDeviceBuffer>
            {
                [nameof(ModelShader.u_BoneBuffer)] = modelData.BoneBuffer
            };

            for (int i = 0; i < materials.Count; i++)
            {
                var pipeline = renderPipelines[i] = CreatePipeline<ModelShader>(renderTarget, materials[i], boundBuffers);
                foreach ((var type, var shadowMaps) in mShadowMaps)
                {
                    string textureArrayName = $"u_{type}ShadowMaps";
                    for (int j = 0; j < shadowMaps.Length; j++)
                    {
                        pipeline.Bind(shadowMaps[j], textureArrayName, j);
                    }
                }
            }

            mLoadedModelInfo.Add(model, new LoadedModelInfo
            {
                RenderPipelines = renderPipelines,
                PointShadowMap = CreatePipeline<PointShadowMap>(mShadowMapRenderTarget, null, boundBuffers)
            });
        }

        private void UpdateBones(Scene scene)
        {
            using var boneUpdateEvent = OptickMacros.Event(category: Category.Animation);
            using (OptickMacros.Event("Pre-bone update"))
            {
                var entityView = scene.ViewEntities(typeof(BoneControllerComponent), typeof(TransformComponent));
                foreach (var entity in entityView)
                {
                    var boneControllerComponent = scene.GetComponent<BoneControllerComponent>(entity);
                    scene.InvokeComponentEvent(boneControllerComponent, entity, ComponentEventID.PreBoneUpdate);
                }
            }

            foreach (ulong entity in scene.ViewEntities(typeof(RenderedModelComponent)))
            {
                var modelData = scene.GetComponent<RenderedModelComponent>(entity);
                if (modelData.ID < 0)
                {
                    continue;
                }

                var boneBuffer = mModelRegistry.Models[modelData.ID].BoneBuffer;
                modelData.BoneController?.Update(boneTransforms => boneBuffer.CopyFromCPU(boneTransforms.Select(mMatrixMath.TranslateMatrix).ToArray(), modelData.BoneOffset * Marshal.SizeOf<BufferMatrix>()));
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

        private void UpdateSceneMatrices(Scene scene, int width, int height)
        {
            using var updateMatricesEvent = OptickMacros.Event(category: Category.Rendering);

            var camera = Scene.Null;
            foreach (ulong entity in scene.ViewEntities(typeof(TransformComponent), typeof(CameraComponent)))
            {
                var component = scene.GetComponent<CameraComponent>(entity);
                if (component.MainCamera)
                {
                    camera = entity;
                    break;
                }
            }

            if (camera != Scene.Null)
            {
                var transform = scene.GetComponent<TransformComponent>(camera);
                var cameraData = scene.GetComponent<CameraComponent>(camera);

                float aspectRatio = (float)width / height;
                float fov = cameraData.FOV * MathF.PI / 180f;
                var projection = mMatrixMath.Perspective(fov, aspectRatio, 0.1f, 100f);

                ComputeCameraVectors(transform.RotationEuler, out Vector3 direction, out Vector3 up);
                var view = mMatrixMath.LookAt(transform.Translation, transform.Translation + direction, up);

                var reflectionView = GetReflectionView<ModelShader>();
                mCameraBuffer?.MapStructure(reflectionView, nameof(ModelShader.u_CameraBuffer), new CameraBufferData
                {
                    Projection = mMatrixMath.TranslateMatrix(projection),
                    View = mMatrixMath.TranslateMatrix(view),
                    Position = transform.Translation
                });
            }
        }

        private Dictionary<LightType, int> UpdateLightBuffer(Scene scene)
        {
            using var updateLightBufferEvent = OptickMacros.Event();

            var lightCounts = new Dictionary<LightType, int>();
            mLightBuffer?.Map(data =>
            {
                var reflectionView = GetReflectionView<ModelShader>();

                var bufferNode = reflectionView.GetResourceNode(nameof(ModelShader.u_LightBuffer));
                if (bufferNode is null)
                {
                    return;
                }

                foreach (ulong entity in scene.ViewEntities(typeof(LightComponent)))
                {
                    var light = scene.GetComponent<LightComponent>(entity);

                    lightCounts.TryAdd(light.Type, 0);
                    int index = lightCounts[light.Type]++;

                    Vector3 position;
                    // no conditional - only point light is defined
                    if (!scene.TryGetComponent(entity, out TransformComponent? transform))
                    {
                        continue;
                    }

                    var transformMatrix = Matrix4x4.Transpose(transform.CreateMatrix(TransformComponents.NonDeformative));
                    position = Vector3.Transform(light.PositionOffset, transformMatrix);

                    switch (light.Type)
                    {
                        case LightType.Point:
                            {
                                if (index >= ModelShader.MaxPointLights)
                                {
                                    throw new NotSupportedException($"Point light limit is {ModelShader.MaxPointLights}");
                                }

                                var pointLightNode = bufferNode.Find($"{nameof(ModelShader.LightBufferData.PointLights)}[{index}]");
                                if (pointLightNode is null)
                                {
                                    continue;
                                }

                                pointLightNode.Set(data, nameof(ModelShader.PointLightData.Diffuse), light.DiffuseColor * light.DiffuseStrength);
                                pointLightNode.Set(data, nameof(ModelShader.PointLightData.Specular), light.SpecularColor * light.SpecularStrength);
                                pointLightNode.Set(data, nameof(ModelShader.PointLightData.Ambient), light.AmbientColor * light.AmbientStrength);
                                pointLightNode.Set(data, nameof(ModelShader.PointLightData.Position), position);

                                var viewDirections = new Vector3[]
                                {
                                    Vector3.UnitX,
                                    -Vector3.UnitX,
                                    Vector3.UnitY,
                                    -Vector3.UnitY,
                                    Vector3.UnitZ,
                                    -Vector3.UnitZ
                                };

                                var projection = mMatrixMath.Perspective(MathF.PI / 2f, 1f, NearPlane, FarPlane);
                                for (int i = 0; i < viewDirections.Length; i++)
                                {
                                    var direction = viewDirections[i];
                                    var up = MathF.Abs(direction.Y) < float.Epsilon ? -Vector3.UnitY : Vector3.UnitZ * MathF.Sign(direction.Y);
                                    var view = mMatrixMath.LookAt(position, position + direction, up);

                                    string fieldName = $"{nameof(ModelShader.PointLightData.ShadowMatrices)}[{i}]";
                                    pointLightNode.Set(data, fieldName, mMatrixMath.TranslateMatrix(projection * view));
                                }

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

                int pointLightCount = lightCounts.GetValueOrDefault(LightType.Point, 0);

                bufferNode.Set(data, nameof(ModelShader.LightBufferData.PointLightCount), pointLightCount);
                bufferNode.Set(data, nameof(ModelShader.LightBufferData.FarPlane), FarPlane);
            });

            return lightCounts;
        }

        private void GeneratePointShadowMaps(Scene scene, Renderer renderer, int lightCount)
        {
            var commandList = renderer.FrameInfo.CommandList;
            for (int i = 0; i < lightCount; i++)
            {
                var shadowMap = mShadowMaps[LightType.Point][i];
                for (int j = 0; j < ModelShader.CubemapFaceCount; j++)
                {
                    var framebuffer = mShadowMapFramebuffers[j];
                    
                    framebuffer.Attachment.TransitionLayout(commandList, framebuffer.Attachment.Layout, DeviceImageLayoutName.DepthStencilAttachment);
                    renderer.BeginRender(mShadowMapRenderTarget, framebuffer.Framebuffer, Vector4.One);

                    RenderScene(scene, renderer, RenderPassType.PointShadowMap, (data, node) =>
                    {
                        node.Set(data, nameof(ModelShader.PushConstantData.LightIndex), i);
                        node.Set(data, nameof(ModelShader.PushConstantData.FaceIndex), j);
                    });

                    renderer.EndRender();
                    framebuffer.Attachment.TransitionLayout(commandList, DeviceImageLayoutName.DepthStencilAttachment, framebuffer.Attachment.Layout);

                    shadowMap.Image.CopyCubeFace(commandList, j, framebuffer.Attachment, shadowMap.Image.Layout, framebuffer.Attachment.Layout);
                }
            }
        }

        private void PrepareForRender(SceneRenderInfo renderInfo)
        {
            using var prepareEvent = OptickMacros.Event(category: Category.Rendering);

            UpdateBones(renderInfo.Scene);
            UpdateSceneMatrices(renderInfo.Scene, renderInfo.Framebuffer.Width, renderInfo.Framebuffer.Height);

            var lightTypeCounts = UpdateLightBuffer(renderInfo.Scene);
            if (lightTypeCounts.TryGetValue(LightType.Point, out int pointLightCount))
            {
                GeneratePointShadowMaps(renderInfo.Scene, renderInfo.Renderer, pointLightCount);
            }
        }

        private void RenderScene(Scene scene, Renderer renderer, RenderPassType passType, PushConstantCallback? pushConstantCallback = null)
        {
            using var renderEvent = OptickMacros.Event(category: Category.Rendering);

            var passShader = passType switch
            {
                RenderPassType.PointShadowMap => typeof(PointShadowMap),
                RenderPassType.Render => typeof(ModelShader),
                _ => throw new ArgumentException("Invalid render pass type!")
            };

            string pushConstantBufferName = ShaderLibrary.ConvertResourceName(nameof(ModelShader.u_PushConstants), passShader, typeof(ModelShader));
            var reflectionView = mReflectionViews[passShader];

            var pushConstantBufferNode = reflectionView.GetResourceNode(pushConstantBufferName);
            if (pushConstantBufferNode is null)
            {
                throw new ArgumentException("Failed to find push constant buffer!");
            }

            foreach (ulong id in scene.ViewEntities(typeof(TransformComponent), typeof(RenderedModelComponent)))
            {
                var transform = scene.GetComponent<TransformComponent>(id);
                var renderedModel = scene.GetComponent<RenderedModelComponent>(id);

                var model = renderedModel.Model;
                if (model is null)
                {
                    continue;
                }

                var info = mLoadedModelInfo[renderedModel.ID];
                foreach (var mesh in model.Submeshes)
                {
                    var pipeline = passType switch
                    {
                        RenderPassType.PointShadowMap => info.PointShadowMap,
                        RenderPassType.Render => info.RenderPipelines[mesh.MaterialIndex],
                        _ => throw new ArgumentException("Invalid render pass type!")
                    };

                    renderer.RenderMesh(model.VertexBuffer, model.IndexBuffer, pipeline,
                                        mesh.IndexOffset, mesh.IndexCount, DeviceBufferIndexType.UInt32,
                                        mapped =>
                                        {
                                            using var pushConstantsEvent = OptickMacros.Category("Push constants", Category.Rendering);
                                            pushConstantCallback?.Invoke(mapped, pushConstantBufferNode);

                                            var model = mMatrixMath.TranslateMatrix(transform.CreateMatrix());
                                            pushConstantBufferNode.Set(mapped, nameof(ModelShader.PushConstantData.Model), model);
                                            pushConstantBufferNode.Set(mapped, nameof(ModelShader.PushConstantData.BoneOffset), renderedModel.BoneOffset);
                                        });
                }
            }
        }

        public void Render(SceneRenderInfo renderInfo)
        {
            using var renderEvent = OptickMacros.Event(category: Category.Rendering);

            if (mWaitForInitialization)
            {
                renderInfo.Renderer.FrameInfo.CommandList.AddSemaphore(mInitializationSemaphore, SemaphoreUsage.Wait);
                mWaitForInitialization = false;
            }

            // prepare for render (update all device buffers)
            PrepareForRender(renderInfo);

            renderInfo.BeginSceneRender.Invoke();
            RenderScene(renderInfo.Scene, renderInfo.Renderer, RenderPassType.Render);
            renderInfo.EndSceneRender.Invoke();
        }

        public IReflectionView GetReflectionView<T>() where T : class => mReflectionViews[typeof(T)];
        public IReflectionView GetReflectionView(Type type) => mReflectionViews[type];

        public bool TryGetReflectionView<T>([NotNullWhen(true)] out IReflectionView? reflectionView) => mReflectionViews.TryGetValue(typeof(T), out reflectionView);
        public bool TryGetReflectionView(Type type, [NotNullWhen(true)] out IReflectionView? reflectionView) => mReflectionViews.TryGetValue(type, out reflectionView);

        private bool mDisposed;
        private readonly IGraphicsContext mContext;

        private readonly ShaderLibrary mShaderLibrary;
        private readonly ModelRegistry mModelRegistry;
        private readonly MatrixMath mMatrixMath;

        private readonly CheckpointStack mCheckpointStack;
        private readonly IDisposable mInitializationSemaphore;
        private bool mWaitForInitialization;

        private readonly Dictionary<int, LoadedModelInfo> mLoadedModelInfo;
        private readonly IDeviceBuffer mCameraBuffer, mLightBuffer;
        private readonly Dictionary<Type, IReflectionView> mReflectionViews;

        private readonly IRenderTarget mShadowMapRenderTarget;
        private readonly ShadowMapFramebuffer[] mShadowMapFramebuffers;
        private readonly Dictionary<LightType, ITexture[]> mShadowMaps;
    }
}
