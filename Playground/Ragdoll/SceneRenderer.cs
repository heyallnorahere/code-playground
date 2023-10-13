using CodePlayground.Graphics;
using Optick.NET;
using Ragdoll.Components;
using Ragdoll.Shaders;
using Silk.NET.Assimp;
using Silk.NET.Vulkan;
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

    public sealed class SceneRenderer : IDisposable
    {
        public SceneRenderer(IGraphicsContext context, ShaderLibrary library, ModelRegistry registry)
        {
            mDisposed = false;
            mContext = context;

            mShaderLibrary = library;
            mModelRegistry = registry;
            mMatrixMath = new MatrixMath(context);

            mLoadedModelInfo = new Dictionary<int, LoadedModelInfo>();
            mReflectionViews = new Dictionary<Type, IReflectionView>();

            foreach (var type in library.CompiledTypes)
            {
                mReflectionViews.Add(type, library.CreateReflectionView(type));
            }

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
                foreach (var pipeline in modelData.Pipelines)
                {
                    pipeline.Dispose();
                }
            }
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

        public void RegisterModel(int model, IRenderTarget renderTarget)
        {
            using var registerModelEvent = OptickMacros.Event();

            var modelData = mModelRegistry.Models[model];
            var materials = modelData.Model.Materials;

            var pipelines = new IPipeline[materials.Count];
            for (int i = 0; i < materials.Count; i++)
            {
                var material = materials[i];
                var pipeline = pipelines[i] = mShaderLibrary.LoadPipeline<ModelShader>(new PipelineDescription
                {
                    RenderTarget = renderTarget,
                    Type = PipelineType.Graphics,
                    FrameCount = Renderer.FrameCount,
                    Specification = material.PipelineSpecification
                });

                pipeline.Bind(mCameraBuffer, nameof(ModelShader.u_CameraBuffer));
                pipeline.Bind(mLightBuffer, nameof(ModelShader.u_LightBuffer));
                pipeline.Bind(modelData.BoneBuffer, nameof(ModelShader.u_BoneBuffer));
                material.Bind(pipeline, nameof(ModelShader.u_MaterialBuffer), textureType => $"u_{textureType}Map");
            }

            mLoadedModelInfo.Add(model, new LoadedModelInfo
            {
                Pipelines = pipelines
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

        private void UpdateLightBuffer(Scene scene)
        {
            using var updateLightBufferEvent = OptickMacros.Event();
            mLightBuffer?.Map(data =>
            {
                var reflectionView = GetReflectionView<ModelShader>();

                var bufferNode = reflectionView.GetResourceNode(nameof(ModelShader.u_LightBuffer));
                if (bufferNode is null)
                {
                    return;
                }

                int currentPointLightIndex = 0;
                foreach (ulong entity in scene.ViewEntities(typeof(LightComponent)))
                {
                    var light = scene.GetComponent<LightComponent>(entity);

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

        private void PrepareForRender(Scene scene, IFramebuffer framebuffer)
        {
            using var prepareEvent = OptickMacros.Event(category: Category.Rendering);

            UpdateBones(scene);
            UpdateSceneMatrices(scene, framebuffer.Width, framebuffer.Height);
            UpdateLightBuffer(scene);
        }

        // todo: generalize for shadow mapping
        private void RenderScene<T>(Scene scene, Renderer renderer) where T : class
        {
            using var renderEvent = OptickMacros.Event(category: Category.Rendering);

            var reflectionView = GetReflectionView<T>();
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
                    var pipeline = info.Pipelines[mesh.MaterialIndex];
                    renderer.RenderMesh(model.VertexBuffer, model.IndexBuffer, pipeline,
                                        mesh.IndexOffset, mesh.IndexCount, DeviceBufferIndexType.UInt32,
                                        mapped =>
                                        {
                                            using var pushConstantsEvent = OptickMacros.Category("Push constants", Category.Rendering);

                                            reflectionView!.MapStructure(mapped, nameof(ModelShader.u_PushConstants), new PushConstantData
                                            {
                                                Model = mMatrixMath.TranslateMatrix(transform.CreateMatrix()),
                                                BoneOffset = renderedModel.BoneOffset
                                            });
                                        });
                }
            }

        }

        public void Render(Scene scene, IFramebuffer framebuffer, Renderer renderer)
        {
            using var renderEvent = OptickMacros.Event(category: Category.Rendering);

            PrepareForRender(scene, framebuffer);
            RenderScene<ModelShader>(scene, renderer);
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

        private readonly Dictionary<int, LoadedModelInfo> mLoadedModelInfo;
        private readonly IDeviceBuffer mCameraBuffer, mLightBuffer;
        private readonly Dictionary<Type, IReflectionView> mReflectionViews;
    }
}
