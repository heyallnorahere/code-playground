using CodePlayground;
using CodePlayground.Graphics;
using ImGuiNET;
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
using VulkanTest.Shaders;

namespace VulkanTest
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public int BoneCount;
        public unsafe fixed int BoneIDs[ModelImportContext.BoneLimitPerVertex];
        public unsafe fixed float BoneWeights[ModelImportContext.BoneLimitPerVertex];
    }

    internal struct PipelineSpecification : IPipelineSpecification
    {
        public PipelineBlendMode BlendMode { get; set; }
        public PipelineFrontFace FrontFace { get; set; }
        public bool EnableDepthTesting { get; set; }
        public bool DisableCulling { get; set; }
    }

    internal struct CameraBufferData
    {
        public Matrix4x4 ViewProjection;
    }

    internal struct PushConstantData
    {
        public Matrix4x4 Model;
        public int BoneTransformOffset;
    }

    internal struct QuadDirection
    {
        public Vector3 Direction { get; set; }
        public Vector3[] OtherDirections { get; set; }
    }

    internal sealed class ModelImportContext : IModelImportContext
    {
        public const int BoneLimitPerVertex = 4;

        public ModelImportContext(IGraphicsContext context)
        {
            mContext = context;
            mStagingBuffers = new List<IDeviceBuffer>();
            mCommandList = null;
        }

        public IGraphicsContext GraphicsContext => mContext;

        public bool ShouldCopyPostLoad => true;
        public int MaxBonesPerVertex => BoneLimitPerVertex;

        [MemberNotNull(nameof(mCommandList))]
        private void BeginCommandList()
        {
            if (mCommandList is not null)
            {
                return;
            }

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            mCommandList = queue.Release();
            mCommandList.Begin();
        }

        public unsafe IDeviceBuffer CreateVertexBuffer(IReadOnlyList<ModelVertex> vertices)
        {
            BeginCommandList();

            var bufferVertices = new Vertex[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                var src = vertices[i];
                var dst = new Vertex
                {
                    Position = src.Position,
                    Normal = src.Normal,
                    UV = src.UV,
                    BoneCount = src.Bones.Count
                };

                for (int j = 0; j < src.Bones.Count; j++)
                {
                    var bone = src.Bones[j];
                    dst.BoneIDs[j] = bone.Index;
                    dst.BoneWeights[j] = bone.Weight;
                }

                bufferVertices[i] = dst;
            }

            int bufferSize = vertices.Count * Marshal.SizeOf<Vertex>();
            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);
            var buffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Vertex, bufferSize);

            stagingBuffer.CopyFromCPU(bufferVertices);
            stagingBuffer.CopyBuffers(mCommandList, buffer, bufferSize);

            mStagingBuffers.Add(stagingBuffer);
            return buffer;
        }

        public IDeviceBuffer CreateIndexBuffer(IReadOnlyList<uint> indices)
        {
            BeginCommandList();

            var bufferIndices = indices.ToArray();
            int bufferSize = indices.Count * Marshal.SizeOf<uint>();

            var buffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Index, bufferSize);
            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);

            stagingBuffer.CopyFromCPU(bufferIndices);
            stagingBuffer.CopyBuffers(mCommandList, buffer, bufferSize);

            mStagingBuffers.Add(stagingBuffer);
            return buffer;
        }

        public ITexture CreateTexture(ReadOnlySpan<byte> data, int width, int height, DeviceImageFormat format)
        {
            BeginCommandList();

            var image = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(width, height),
                Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = format
            });

            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, data.Length);
            stagingBuffer.CopyFromCPU(data);

            var newLayout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            image.TransitionLayout(mCommandList, image.Layout, newLayout);
            image.CopyFromBuffer(mCommandList, stagingBuffer, newLayout);
            image.Layout = newLayout;

            mStagingBuffers.Add(stagingBuffer);
            return image.CreateTexture(true);
        }

        public ITexture LoadTexture(string texturePath, string modelPath, bool loadedFromFile, ISamplerSettings samplerSettings)
        {
            Image<Rgba32> image;
            if (loadedFromFile)
            {
                var path = Path.IsPathFullyQualified(texturePath) ? texturePath : Path.GetFullPath(Path.Join(Path.GetDirectoryName(modelPath), texturePath));
                image = Image.Load<Rgba32>(path);
            }
            else
            {
                if (Path.IsPathFullyQualified(texturePath))
                {
                    throw new ArgumentException("Cannot load a fully qualified path!");
                }

                var relativePath = Path.Join(Path.GetDirectoryName(modelPath), texturePath).Replace('\\', '/').Replace("./", string.Empty);

                int directoryEscapePosition;
                while ((directoryEscapePosition = relativePath.LastIndexOf("..")) > 0)
                {
                    int separatorPosition = relativePath.LastIndexOf("/", directoryEscapePosition - 2);
                    if (separatorPosition < 0)
                    {
                        separatorPosition = 0;
                    }

                    relativePath = relativePath.Remove(separatorPosition, directoryEscapePosition + 2 - separatorPosition);
                }

                var stream = VulkanTestApp.GetResourceStream(relativePath);
                image = Image.Load<Rgba32>(stream ?? throw new FileNotFoundException());
            }

            var deviceImage = mContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = image.Size,
                Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = DeviceImageFormat.RGBA8_UNORM
            });

            var pixelData = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(pixelData);

            var stagingBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, pixelData.Length * Marshal.SizeOf<Rgba32>());
            stagingBuffer.CopyFromCPU(pixelData);

            var newLayout = deviceImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            BeginCommandList();
            deviceImage.TransitionLayout(mCommandList, deviceImage.Layout, newLayout);
            deviceImage.CopyFromBuffer(mCommandList, stagingBuffer, newLayout);
            deviceImage.Layout = newLayout;

            mStagingBuffers.Add(stagingBuffer);
            return deviceImage.CreateTexture(true);
        }

        public void CopyBuffers()
        {
            if (mCommandList is null)
            {
                return;
            }

            mCommandList.End();

            var queue = mContext.Device.GetQueue(CommandQueueFlags.Transfer);
            queue.Submit(mCommandList, true);

            mStagingBuffers.ForEach(buffer => buffer.Dispose());
            mStagingBuffers.Clear();

            mCommandList = null;
        }

        private ICommandList? mCommandList;
        private readonly List<IDeviceBuffer> mStagingBuffers;
        private readonly IGraphicsContext mContext;
    }

    internal struct Transform
    {
        public Transform()
        {
            Translation = new Vector3(0f);
            Rotation = new Vector3(0f);
            Scale = new Vector3(1f);
        }

        public Vector3 Translation;
        public Vector3 Rotation;
        public Vector3 Scale;

        public static implicit operator Matrix4x4(Transform transform)
        {
            var rotation = transform.Rotation * MathF.PI / 180f;
            var rotationMatrix = Matrix4x4.CreateRotationX(rotation.X) *
                                 Matrix4x4.CreateRotationY(rotation.Y) *
                                 Matrix4x4.CreateRotationZ(rotation.Z);

            return Matrix4x4.Transpose(Matrix4x4.CreateScale(transform.Scale) *
                                       rotationMatrix *
                                       Matrix4x4.CreateTranslation(transform.Translation));
        }
    }

    [ApplicationTitle("Vulkan Test")]
    [ApplicationGraphicsAPI(AppGraphicsAPI.Vulkan)]
    public class VulkanTestApp : GraphicsApplication
    {
        public static int Main(string[] args) => RunApplication<VulkanTestApp>(args);

        public VulkanTestApp()
        {
            Load += OnLoad;
            InputReady += OnInputReady;
            Closing += OnClose;
            Update += OnUpdate;
            Render += OnRender;

            mBoneTransformations = null;
            mSelectedBone = -1;
        }

        public static Stream? GetResourceStream(string path)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceId = $"{assembly.GetName().Name}.Resources.{path.Replace('\\', '/').Replace('/', '.')}";
            return assembly.GetManifestResourceStream(resourceId);
        }

        private static Model? LoadModelResource(string path, IModelImportContext importContext)
        {
            var stream = GetResourceStream(path);
            if (stream is null)
            {
                return null;
            }

            var buffer = new byte[stream.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                totalRead += stream.Read(buffer, totalRead, buffer.Length - totalRead);
            }

            return Model.Load(buffer, path, importContext);
        }

        private IDeviceImage EmbossImage(IGraphicsContext context, IRenderer renderer, ICommandList commandList)
        {
            var pipeline = mShaderLibrary!.LoadPipeline<TestCompute>(new PipelineDescription
            {
                Type = PipelineType.Compute,
                FrameCount = 1
            });

            var stream = GetResourceStream("Models/textures/hammer_Metallic.png");
            if (stream is null)
            {
                throw new FileNotFoundException("Could not image to emboss!");
            }

            var image = Image.Load<Rgba32>(stream);
            var sourceImage = context.CreateDeviceImage(new DeviceImageInfo
            {
                Size = image.Size,
                Usage = DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Storage,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            });

            var destinationImage = context.CreateDeviceImage(new DeviceImageInfo
            {
                Size = image.Size,
                Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.Storage,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            });

            var storageLayout = sourceImage.GetLayout(DeviceImageLayoutName.ComputeStorage);
            sourceImage.TransitionLayout(commandList, sourceImage.Layout, storageLayout);
            destinationImage.TransitionLayout(commandList, destinationImage.Layout, storageLayout);
            sourceImage.Layout = destinationImage.Layout = storageLayout;

            int bufferSize = image.Width * image.Height * Marshal.SizeOf<Rgba32>();
            var buffer = new byte[bufferSize];
            image.CopyPixelDataTo(buffer);

            var stagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);
            stagingBuffer.CopyFromCPU(buffer);

            commandList.PushStagingObject(stagingBuffer);
            commandList.PushStagingObject(pipeline);
            commandList.PushStagingObject(sourceImage);

            sourceImage.CopyFromBuffer(commandList, stagingBuffer, storageLayout);
            pipeline.Bind(sourceImage, nameof(TestCompute.u_Input));
            pipeline.Bind(destinationImage, nameof(TestCompute.u_Result));
            pipeline.Bind(commandList, 0);

            renderer.DispatchCompute(commandList, image.Width / TestCompute.BlockSize, image.Height / TestCompute.BlockSize, 1);
            return destinationImage;
        }

        private void OnLoad()
        {
            var context = CreateGraphicsContext();
            var swapchain = context.Swapchain;

            if (swapchain is not null)
            {
                swapchain.VSync = true; // enable vsync
            }

            mShaderLibrary = new ShaderLibrary(context, GetType().Assembly);
            mRenderer = context.CreateRenderer();

            mModel = LoadModelResource("Models/rigged-character.fbx", new ModelImportContext(context));
            if (mModel is null)
            {
                throw new InvalidOperationException("Failed to load model!");
            }

            mPipelines = new IPipeline[mModel.Materials.Count];
            mReflectionView = mShaderLibrary.CreateReflectionView<TestShader>();

            string cameraBufferName = nameof(TestShader.u_CameraBuffer);
            string boneTransformBufferName = nameof(TestShader.u_BoneTransformBuffer);

            int cameraBufferSize = mReflectionView.GetBufferSize(cameraBufferName);
            if (cameraBufferSize < 0)
            {
                throw new ArgumentException($"Failed to find buffer \"{cameraBufferName}\"");
            }

            int boneTransformBufferSize = mReflectionView.GetBufferSize(boneTransformBufferName);
            if (boneTransformBufferSize < 0)
            {
                throw new ArgumentException($"Failed to find buffer \"{boneTransformBufferName}\"");
            }

            mCameraBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, cameraBufferSize);
            mBoneTransformBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, boneTransformBufferSize);

            for (int i = 0; i < mModel.Materials.Count; i++)
            {
                var material = mModel.Materials[i];
                var pipeline = mShaderLibrary.LoadPipeline<TestShader>(new PipelineDescription
                {
                    RenderTarget = swapchain?.RenderTarget,
                    Type = PipelineType.Graphics,
                    FrameCount = swapchain?.FrameCount ?? 0,
                    Specification = material.PipelineSpecification
                });

                pipeline.Bind(mCameraBuffer, cameraBufferName, 0);
                pipeline.Bind(mBoneTransformBuffer, boneTransformBufferName, 0);
                material.Bind(pipeline, nameof(TestShader.u_MaterialBuffer), textureType => $"u_{textureType}Map");

                mPipelines[i] = pipeline;
            }

            // compute shader test
            {
                var queue = context.Device.GetQueue(CommandQueueFlags.Transfer | CommandQueueFlags.Compute);
                var commandList = queue.Release();
                commandList.Begin();

                var result = EmbossImage(context, mRenderer, commandList);
                commandList.PushStagingObject(result);

                var imageSize = result.Size;
                int bufferSize = imageSize.Width * imageSize.Height * Marshal.SizeOf<Rgba32>();

                var stagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, bufferSize);
                result.CopyToBuffer(commandList, stagingBuffer, result.Layout);
                commandList.PushStagingObject(stagingBuffer);

                commandList.End();
                queue.Submit(commandList, true);

                var imageBuffer = new byte[bufferSize];
                stagingBuffer.CopyToCPU(imageBuffer);

                var image = Image.LoadPixelData<Rgba32>(imageBuffer, imageSize.Width, imageSize.Height);
                image.SaveAsPng("result.png");
            }

            InitializeImGui();
        }

        private void OnInputReady() => InitializeImGui();

        private void InitializeImGui()
        {
            var graphicsContext = GraphicsContext;
            var inputContext = InputContext;
            var window = RootWindow;
            var swapchain = graphicsContext?.Swapchain;

            if (graphicsContext is null || inputContext is null || window is null || mImGuiController is not null || swapchain is null)
            {
                return;
            }

            mImGuiController = new ImGuiController(graphicsContext, inputContext, window, swapchain.RenderTarget, swapchain.FrameCount);
            ImGui.StyleColorsDark();

            var device = graphicsContext.Device;
            var queue = device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            mImGuiController.LoadFontAtlas(commandList);
            commandList.End();

            queue.Submit(commandList, true);
        }

        private void OnClose()
        {
            var device = GraphicsContext?.Device;
            device?.ClearQueues();

            if (mPipelines is not null)
            {
                for (int i = 0; i < mPipelines.Length; i++)
                {
                    mPipelines[i].Dispose();
                }
            }

            mImGuiController?.Dispose();
            mCameraBuffer?.Dispose();
            mBoneTransformBuffer?.Dispose();
            mModel?.Dispose();

            mShaderLibrary?.Dispose();
            GraphicsContext?.Dispose();
        }

        private void OnUpdate(double delta)
        {
            mImGuiController!.NewFrame(delta);
            mTime += (float)delta;

            var swapchain = GraphicsContext?.Swapchain;
            if (swapchain is null)
            {
                return;
            }

            const float radius = 5f;
            float x = -MathF.Cos(mTime) * radius;
            float y = MathF.Pow(radius, 1.75f);
            float z = MathF.Sin(mTime) * radius;

            float aspectRatio = swapchain.Width / (float)swapchain.Height;
            var math = new MatrixMath(GraphicsContext!);
            var projection = math.Perspective(MathF.PI / 4f, aspectRatio, 0.1f, 100f);
            var view = math.LookAt(new Vector3(x, y, z), Vector3.UnitY * (y - radius), Vector3.UnitY);

            mCameraBuffer!.MapStructure(mReflectionView!, nameof(TestShader.u_CameraBuffer), new CameraBufferData
            {
                ViewProjection = Matrix4x4.Transpose(projection * view)
            });

            // todo: compute via animationcontroller or something
            var skeleton = mModel?.Skeleton;
            if (skeleton is not null)
            {
                if (skeleton.BoneCount > TestShader.MaxBones)
                {
                    throw new InvalidOperationException("Skeleton has more bones than is supported!");
                }

                if (mBoneTransformations is null)
                {
                    mBoneTransformations = new Transform[skeleton.BoneCount];
                    Array.Fill(mBoneTransformations, new Transform());
                }

                ImGui.Begin("Skeleton");
                if (ImGui.BeginCombo("Bone", mSelectedBone < 0 ? "--None--" : skeleton.GetName(mSelectedBone)))
                {
                    for (int i = 0; i < skeleton.BoneCount; i++)
                    {
                        bool isSelected = mSelectedBone == i;

                        var name = skeleton.GetName(i);
                        if (ImGui.Selectable(name, isSelected))
                        {
                            mSelectedBone = i;
                        }

                        if (isSelected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }

                    ImGui.EndCombo();
                }

                var globalTransforms = new List<Matrix4x4>();
                var result = new Matrix4x4[skeleton.BoneCount];
                for (int i = 0; i < skeleton.BoneCount; i++)
                {
                    var manualTransform = mBoneTransformations[i];
                    if (mSelectedBone == i)
                    {
                        bool update = false;
                        update |= ImGui.DragFloat3("Translation", ref manualTransform.Translation);
                        update |= ImGui.DragFloat3("Rotation", ref manualTransform.Rotation);
                        update |= ImGui.DragFloat3("Scale", ref manualTransform.Scale);

                        if (update)
                        {
                            mBoneTransformations[i] = manualTransform;
                        }
                    }

                    int parent = skeleton.GetParent(i);
                    var parentTransform = parent < 0 ? skeleton.GetParentTransform(i) : globalTransforms[parent];

                    var nodeTransform = skeleton.GetTransform(i) * manualTransform;
                    var globalTransform = parentTransform * nodeTransform;

                    var offsetMatrix = skeleton.GetOffsetMatrix(i);
                    var boneTransform = globalTransform * offsetMatrix;

                    globalTransforms.Add(globalTransform);
                    result[i] = Matrix4x4.Transpose(boneTransform);
                }

                ImGui.End();
                mBoneTransformBuffer!.CopyFromCPU(result);
            }
        }

        private void OnRender(FrameRenderInfo renderInfo)
        {
            if (renderInfo.CommandList is null || renderInfo.RenderTarget is null || renderInfo.Framebuffer is null)
            {
                return;
            }

            var clearColor = new Vector4(0f, 0f, 0f, 1f);
            renderInfo.RenderTarget.BeginRender(renderInfo.CommandList, renderInfo.Framebuffer, clearColor);

            mModel!.VertexBuffer.BindVertices(renderInfo.CommandList, 0);
            mModel!.IndexBuffer.BindIndices(renderInfo.CommandList, DeviceBufferIndexType.UInt32);

            var model = Matrix4x4.CreateScale(0.01f);
            var submeshes = mModel!.Submeshes;
            for (int i = 0; i < submeshes.Count; i++)
            {
                var submesh = submeshes[i];
                var pipeline = mPipelines![submesh.MaterialIndex];

                pipeline.Bind(renderInfo.CommandList, renderInfo.CurrentImage);
                pipeline.PushConstants(renderInfo.CommandList, mapped =>
                {
                    mReflectionView!.MapStructure(mapped, nameof(TestShader.u_PushConstants), new PushConstantData
                    {
                        Model = Matrix4x4.Transpose(model * submesh.Transform),
                        BoneTransformOffset = 0
                    });
                });

                mRenderer!.RenderIndexed(renderInfo.CommandList, submesh.IndexOffset, submesh.IndexCount);
            }

            mImGuiController!.Render(renderInfo.CommandList, mRenderer!, renderInfo.CurrentImage);
            renderInfo.RenderTarget.EndRender(renderInfo.CommandList);
        }

        private ShaderLibrary? mShaderLibrary;
        private ImGuiController? mImGuiController;
        private IPipeline[]? mPipelines;
        private IReflectionView? mReflectionView;
        private IDeviceBuffer? mCameraBuffer, mBoneTransformBuffer;
        private Model? mModel;
        private Transform[]? mBoneTransformations;
        private int mSelectedBone;
        private IRenderer? mRenderer;
        private float mTime;
    }
}
