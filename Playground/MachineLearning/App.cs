using CodePlayground;
using CodePlayground.Graphics;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Optick.NET;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace MachineLearning
{
    internal struct DatasetSource
    {
        public string Images;
        public string Labels;
    }

    public enum DatasetType
    {
        Training,
        Testing
    }

    [ApplicationTitle("Machine learning test")]
    internal sealed class App : GraphicsApplication
    {
        public static new App Instance => (App)Application.Instance;
        public static Random RNG => sRandom;
        public static JsonSerializer Serializer => JsonSerializer.Create(sSettings);
        
        private static readonly Random sRandom;
        private static readonly JsonSerializerSettings sSettings;
        private static readonly IReadOnlyDictionary<DatasetType, DatasetSource> sDatasetSources;

        static App()
        {
            sRandom = new Random();
            sSettings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new SnakeCaseNamingStrategy()
                },
                Formatting = Formatting.Indented
            };

            sDatasetSources = new Dictionary<DatasetType, DatasetSource>
            {
                [DatasetType.Training] = new DatasetSource
                {
                    Images = "http://yann.lecun.com/exdb/mnist/train-images-idx3-ubyte.gz",
                    Labels = "http://yann.lecun.com/exdb/mnist/train-labels-idx1-ubyte.gz"
                },
                [DatasetType.Testing] = new DatasetSource
                {
                    Images = "http://yann.lecun.com/exdb/mnist/t10k-images-idx3-ubyte.gz",
                    Labels = "http://yann.lecun.com/exdb/mnist/t10k-labels-idx1-ubyte.gz"
                }
            };
        }

        public App()
        {
            mExistingSemaphores = new Queue<IDisposable>();
            mSignaledSemaphores = new List<IDisposable>();

            mPassIndex = -1;
            mSelectedImage = 0;
            mInputString = string.Empty;

            Load += OnLoad;
            InputReady += OnInputReady;
            Closing += OnClose;

            Update += OnUpdate;
            Render += OnRender;
        }

        [MemberNotNull(nameof(mDataset))]
        public void LoadDataset(DatasetType type)
        {
            var source = sDatasetSources[type];
            mDataset = Dataset.Pull(source.Images, source.Labels);

            mSelectedDataset = type;
        }

        private IDisposable GetSemaphore()
        {
            if (!mExistingSemaphores.TryDequeue(out IDisposable? semaphore))
            {
                semaphore = GraphicsContext!.CreateSemaphore();
            }

            return semaphore;
        }

        public void SignalSemaphore(ICommandList commandList)
        {
            var semaphore = GetSemaphore();
            mSignaledSemaphores.Add(semaphore);

            commandList.AddSemaphore(semaphore, SemaphoreUsage.Signal);
        }

        #region Events

        private void OnLoad()
        {
            var context = CreateGraphicsContext();
            context.Swapchain.VSync = true;

            mLibrary = new ShaderLibrary(context, Assembly.GetExecutingAssembly());
            mRenderer = context.CreateRenderer();

            InitializeOptick();
            InitializeImGui();

            // load testing dataset as default
            LoadDataset(DatasetType.Testing);

            mComputeFence = context.CreateFence(true);
            mDisplayedTexture = context.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(mDataset.Width, mDataset.Height),
                Usage = DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            }).CreateTexture(true);

            const string networkFileName = "network.json";
            if (File.Exists(networkFileName))
            {
                using var stream = new FileStream(networkFileName, FileMode.Open, FileAccess.Read);
                mNetwork = Network.Load(stream);

                int inputCount = mNetwork.LayerSizes[0];
                if (inputCount != mDataset.InputSize)
                {
                    throw new ArgumentException("Input size mismatch!");
                }
            }
            else
            {
                mNetwork = new Network(new int[]
                {
                    mDataset.InputSize, // input
                    64, // arbitrary hidden layer sizes
                    16,
                    10
                });
            }

            var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            using (commandList.Context(GPUQueueType.Transfer))
            {
                var image = mDisplayedTexture.Image;
                var layout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

                image.TransitionLayout(commandList, image.Layout, layout);
                image.Layout = layout;
            }

            SignalSemaphore(commandList);

            commandList.End();
            queue.Submit(commandList);
        }

        private void OnInputReady() => InitializeImGui();
        private void InitializeImGui()
        {
            var graphicsContext = GraphicsContext;
            var inputContext = InputContext;
            var window = RootWindow;

            if (mImGui is not null || window is null || graphicsContext is null || inputContext is null)
            {
                return;
            }

            var queue = graphicsContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            using (commandList.Context(GPUQueueType.Transfer))
            {
                mImGui = new ImGuiController(graphicsContext, inputContext, window, graphicsContext.Swapchain.RenderTarget, SynchronizationFrames);
                mImGui.LoadFontAtlas(commandList);
            }

            SignalSemaphore(commandList);

            commandList.End();
            queue.Submit(commandList);
        }

        private void OnClose()
        {
            var context = GraphicsContext;
            context?.Device?.ClearQueues();

            while (mExistingSemaphores.Count > 0)
            {
                var semaphore = mExistingSemaphores.Dequeue();
                semaphore.Dispose();
            }

            foreach (var semaphore in mSignaledSemaphores)
            {
                semaphore.Dispose();
            }

            mImGui?.Dispose();
            mDisplayedTexture?.Dispose();
            mComputeFence?.Dispose();

            mLibrary?.Dispose();
            GraphicsContext?.Dispose();
        }

        private void OnUpdate(double delta)
        {
            mImGui?.NewFrame(delta);

            DatasetMenu();
            NetworkMenu();
        }

        private void OnRender(FrameRenderInfo renderInfo)
        {
            var commandList = renderInfo.CommandList!;
            var renderTarget = renderInfo.RenderTarget!;

            foreach (var semaphore in mSignaledSemaphores)
            {
                commandList.AddSemaphore(semaphore, SemaphoreUsage.Wait);
            }

            renderTarget.BeginRender(commandList, renderInfo.Framebuffer!, Vector4.Zero);
            mImGui?.Render(commandList, mRenderer!, mCurrentFrame);
            renderTarget.EndRender(commandList);

            mCurrentFrame = (mCurrentFrame + 1) % SynchronizationFrames;
            mSignaledSemaphores.Clear();
        }

        #endregion
        #region Menus

        private void DatasetMenu()
        {
            ImGui.Begin("Dataset");

            int imageCount = mDataset?.Count ?? 0;
            ImGui.Text($"{imageCount} images loaded");

            if (ImGui.BeginCombo("##dataset-type", mSelectedDataset.ToString()))
            {
                var types = Enum.GetValues<DatasetType>();
                foreach (var type in types)
                {
                    bool isSelected = type == mSelectedDataset;
                    bool isDisabled = !sDatasetSources.ContainsKey(type);

                    if (isDisabled)
                    {
                        ImGui.BeginDisabled();
                    }

                    if (ImGui.Selectable(type.ToString(), isSelected))
                    {
                        mSelectedDataset = type;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }

                    if (isDisabled)
                    {
                        ImGui.EndDisabled();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button("Load dataset"))
            {
                LoadDataset(mSelectedDataset);
            }

            ImGui.InputInt("##selected-image", ref mSelectedImage);
            ImGui.SameLine();

            if (ImGui.Button("Load image"))
            {
                if (mSelectedImage <= 0 || mSelectedImage > imageCount)
                {
                    throw new IndexOutOfRangeException();
                }

                int imageIndex = mSelectedImage - 1;
                var imageData = mDataset!.GetImageData(imageIndex, 4); // rgba

                var context = GraphicsContext!;
                var buffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, imageData.Length);
                buffer.CopyFromCPU(imageData);

                var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);
                var commandList = queue.Release();

                commandList.Begin();
                using (commandList.Context(GPUQueueType.Transfer))
                {
                    var image = mDisplayedTexture!.Image;
                    image.CopyFromBuffer(commandList, buffer, image.Layout);
                }

                SignalSemaphore(commandList);
                commandList.PushStagingObject(buffer);

                commandList.End();
                queue.Submit(commandList);
            }

            var regionAvailable = ImGui.GetContentRegionAvail();
            ImGui.Image(mImGui!.GetTextureID(mDisplayedTexture!), Vector2.One * regionAvailable.X);

            ImGui.End();
        }

        private void NetworkMenu()
        {
            bool fenceSignaled = mComputeFence!.IsSignaled();
            if (mReadBuffer && fenceSignaled)
            {
                mOutputs = NetworkDispatcher.GetConfidenceValues(mActivationBuffer!, mStride, mActivationOffset, mPassCount, mNetwork!.LayerSizes);
                mPassIndex = 0;

                mReadBuffer = false;
            }

            ImGui.Begin("Network");

            ImGui.Text("Enter image numbers to pass through the neural network, separated by commas; spaces allowed.");
            ImGui.InputText("##input-string", ref mInputString, 512);
            ImGui.SameLine();

            bool isDisabled = mReadBuffer || !fenceSignaled;
            if (isDisabled)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Run"))
            {
                if (mDataset is null)
                {
                    throw new InvalidOperationException("No dataset loaded!");
                }

                var imageNumbers = mInputString.Replace(" ", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                var inputs = new float[mPassCount = imageNumbers.Length][];

                for (int i = 0; i < imageNumbers.Length; i++)
                {
                    int number = imageNumbers[i];
                    if (number <= 0 || number > mDataset.Count)
                    {
                        throw new ArgumentException("Invalid image number!");
                    }

                    int imageIndex = number - 1;
                    inputs[i] = mDataset.GetInput(imageIndex);
                }

                var context = GraphicsContext!;
                var queue = context.Device.GetQueue(CommandQueueFlags.Compute);
                var commandList = queue.Release();

                commandList.Begin();
                using (commandList.Context(GPUQueueType.Compute))
                {
                    mActivationBuffer?.Dispose();
                    mActivationBuffer = NetworkDispatcher.Dispatch(commandList, mNetwork!, inputs, out mStride, out mActivationOffset);
                }

                mComputeFence.Reset();
                mReadBuffer = true;

                commandList.End();
                queue.Submit(commandList, fence: mComputeFence);
            }

            if (isDisabled)
            {
                ImGui.EndDisabled();
            }

            if (ImGui.CollapsingHeader("Results"))
            {
                isDisabled = mOutputs is null;
                if (isDisabled)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.BeginCombo("Pass results", $"Pass #{mPassIndex + 1}"))
                {
                    for (int i = 0; i < mOutputs!.Length; i++)
                    {
                        bool isSelected = i == mPassIndex;
                        if (ImGui.Selectable($"Pass #{i + 1}", isSelected))
                        {
                            mPassIndex = i;
                        }

                        if (isSelected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }

                    ImGui.EndCombo();
                }

                if (mPassIndex >= 0)
                {
                    var output = mOutputs![mPassIndex];
                    for (int i = 0; i < output.Length; i++)
                    {
                        float confidence = output[i] * 100f;
                        ImGui.TextUnformatted($"{i}: {confidence}% confident");
                    }
                }

                if (isDisabled)
                {
                    ImGui.EndDisabled();
                }
            }

            ImGui.End();
        }

        #endregion

        public ShaderLibrary Library => mLibrary!;
        public IRenderer Renderer => mRenderer!;

        private ImGuiController? mImGui;
        private IRenderer? mRenderer;

        private Network? mNetwork;
        private Dataset? mDataset;

        private ShaderLibrary? mLibrary;
        private int mCurrentFrame;

        private DatasetType mSelectedDataset;
        private int mSelectedImage;
        private ITexture? mDisplayedTexture;

        private string mInputString;
        private float[][]? mOutputs;
        private bool mReadBuffer;
        private IDeviceBuffer? mActivationBuffer;
        private int mPassIndex;
        private int mStride, mActivationOffset, mPassCount;
        private IFence? mComputeFence;

        private readonly Queue<IDisposable> mExistingSemaphores;
        private readonly List<IDisposable> mSignaledSemaphores;
    }
}