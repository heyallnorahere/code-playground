using CodePlayground;
using CodePlayground.Graphics;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
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

    internal enum DatasetType
    {
        Training,
        Testing
    }

    internal struct DatasetImageSampler : ISamplerSettings
    {
        public AddressMode AddressMode => AddressMode.Repeat;
        public SamplerFilter Filter => SamplerFilter.Nearest;
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
                Formatting = Formatting.Indented,
                Converters = new JsonConverter[]
                {
                    new StringEnumConverter()
                }
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

            // load testing dataset as default
            mSelectedDataset = DatasetType.Testing;

            mAverageAbsoluteCost = float.PositiveInfinity;
            mMinimumAverageCost = 0f;
            mUseMinimumAverageCost = false;

            Load += OnLoad;
            InputReady += OnInputReady;
            Closing += OnClose;

            Update += OnUpdate;
            Render += OnRender;
        }

        public override bool ShouldRunHeadless => mSelectedTestImages is not null;

        protected override void ParseArguments()
        {
            var args = CommandLineArguments;
            if (args.Length == 0)
            {
                return;
            }

            string selectedDataString = string.Empty;
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    selectedDataString += ' ';
                }

                selectedDataString += args[i];
            }

            int separatorPosition = selectedDataString.IndexOf(':');
            if (separatorPosition < 0)
            {
                throw new ArgumentException("Malformed selector string!");
            }

            mSelectedTestImages = selectedDataString[(separatorPosition + 1)..].Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            mSelectedDataset = Enum.Parse<DatasetType>(selectedDataString[..separatorPosition], true);
        }

        [MemberNotNull(nameof(mDataset))]
        public void LoadDataset(DatasetType type)
        {
            var source = sDatasetSources[type];
            var cachePath = $"data/{type.ToString().ToLower()}/";

            var imageSource = new DatasetFileSource
            {
                Url = source.Images,
                Cache = cachePath + "images"
            };

            var labelSource = new DatasetFileSource
            {
                Url = source.Labels,
                Cache = cachePath + "labels"
            };

            mDataset = MNISTDatabase.Load(imageSource, labelSource);
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

        private void OnBatchResults(TrainerBatchResults results)
        {
            mAverageAbsoluteCost = results.AverageAbsoluteCost;
            if (mUseMinimumAverageCost && mAverageAbsoluteCost < mMinimumAverageCost)
            {
                mTrainer?.Stop();
            }
        }

        private const string networkFileName = "network.json";
        private void OnLoad()
        {
            var context = CreateGraphicsContext();

            var swapchain = context.Swapchain;
            if (swapchain is not null)
            {
                swapchain.VSync = true;
            }

            InitializeOptick();
            InitializeImGui();

            mLibrary = new ShaderLibrary(context, Assembly.GetExecutingAssembly());
            mRenderer = context.CreateRenderer();
            NetworkDispatcher.Initialize(mRenderer, mLibrary);

            mTrainer = new Trainer(context, 100, 0.1f); // initial batch size and learning rate
            mTrainer.OnBatchResults += OnBatchResults;

            LoadDataset(mSelectedDataset);

            mComputeFence = context.CreateFence(true);
            mDisplayedTexture = context.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(mDataset.Width, mDataset.Height),
                Usage = DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            }).CreateTexture(true, new DatasetImageSampler());

            if (File.Exists(networkFileName))
            {
                using var stream = new FileStream(networkFileName, FileMode.Open, FileAccess.Read);
                mNetwork = Network.Load(stream);

                int inputCount = mNetwork.LayerSizes[0];
                if (inputCount != mDataset.InputCount)
                {
                    throw new ArgumentException("Input size mismatch!");
                }
            }
            else
            {
                mNetwork = new Network(new int[]
                {
                    mDataset.InputCount, // input
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
            var renderTarget = graphicsContext?.Swapchain?.RenderTarget;

            if (mImGui is not null || window is null || graphicsContext is null || inputContext is null || renderTarget is null)
            {
                return;
            }

            var queue = graphicsContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            using (commandList.Context(GPUQueueType.Transfer))
            {
                mImGui = new ImGuiController(graphicsContext, inputContext, window, renderTarget, SynchronizationFrames);
                mImGui.LoadFontAtlas(commandList);
            }

            SignalSemaphore(commandList);

            commandList.End();
            queue.Submit(commandList);
        }

        private void OnClose()
        {
            if (mNetwork is not null)
            {
                using var stream = new FileStream(networkFileName, FileMode.Create, FileAccess.Write);
                Network.Save(mNetwork, stream);
            }

            var context = GraphicsContext;
            context?.Device?.ClearQueues();

            while (mExistingSemaphores.TryDequeue(out IDisposable? semaphore))
            {
                semaphore.Dispose();
            }

            foreach (var semaphore in mSignaledSemaphores)
            {
                semaphore.Dispose();
            }

            mImGui?.Dispose();
            mDisplayedTexture?.Dispose();
            mComputeFence?.Dispose();
            mBufferData?.ActivationBuffer?.Dispose();
            mTrainer?.Dispose();

            mLibrary?.Dispose();
            GraphicsContext?.Dispose();
        }

        private void OnUpdate(double delta)
        {
            mImGui?.NewFrame(delta);
            mTrainer?.Update();

            DatasetMenu();
            NetworkMenu();
            TrainingMenu();
        }

        private void RunNeuralNetwork(int[] imageNumbers)
        {
            // just run headless
            var inputs = imageNumbers.Select(number => mDataset!.GetInput(number - 1)).ToArray();

            var context = GraphicsContext!;
            var queue = context.Device.GetQueue(CommandQueueFlags.Compute);

            var commandList = queue.Release();
            commandList.Begin();

            var data = NetworkDispatcher.CreateBuffers(mNetwork!, inputs.Length);
            using (commandList.Context(GPUQueueType.Compute))
            {
                NetworkDispatcher.ForwardPropagation(commandList, data, inputs);

                commandList.PushStagingObject(data.PreSigmoidBuffer);
                commandList.PushStagingObject(data.SizeBuffer);
                commandList.PushStagingObject(data.DataBuffer);
                commandList.PushStagingObject(data.DeltaBuffer);
            }

            commandList.End();
            queue.Submit(commandList);
            queue.ClearCache();

            using (data.ActivationBuffer)
            {
                var confidence = NetworkDispatcher.GetConfidenceValues(data);
                for (int i = 0; i < confidence.Length; i++)
                {
                    var passConfidence = confidence[i];
                    Console.WriteLine($"Pass #{i + 1}");

                    for (int j = 0; j < passConfidence.Length; j++)
                    {
                        Console.WriteLine($"{j}: {passConfidence[j] * 100f}%");
                    }
                }
            }
        }

        private void OnRender(FrameRenderInfo renderInfo)
        {
            if (mSelectedTestImages is not null)
            {
                RunNeuralNetwork(mSelectedTestImages);
                return;
            }

            var commandList = renderInfo.CommandList!;
            var renderTarget = renderInfo.RenderTarget!;

            foreach (var semaphore in mSignaledSemaphores)
            {
                commandList.AddSemaphore(semaphore, SemaphoreUsage.Wait);
                mExistingSemaphores.Enqueue(semaphore);
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
                mOutputs = NetworkDispatcher.GetConfidenceValues(mBufferData!.Value);
                mPassIndex = 0;

                mReadBuffer = false;
            }

            ImGui.Begin("Network");

            ImGui.TextWrapped("Enter image numbers to pass through the neural network, separated by commas; spaces allowed.");
            ImGui.TextWrapped("Note: it is not recommended to feed the training set through the network to test. Instead, use the test dataset.");

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
                var inputs = new float[imageNumbers.Length][];

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
                    var data = NetworkDispatcher.CreateBuffers(mNetwork!, inputs.Length);
                    NetworkDispatcher.ForwardPropagation(commandList, data, inputs);

                    commandList.PushStagingObject(data.PreSigmoidBuffer);
                    commandList.PushStagingObject(data.SizeBuffer);
                    commandList.PushStagingObject(data.DataBuffer);
                    commandList.PushStagingObject(data.DeltaBuffer);

                    mBufferData?.ActivationBuffer?.Dispose();
                    mBufferData = data;
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

        private void TrainingMenu()
        {
            ImGui.Begin("Training");

            ImGui.Checkbox("##use-minimum", ref mUseMinimumAverageCost);
            ImGui.SameLine();

            var minAverageFlags = mUseMinimumAverageCost ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly;
            ImGui.InputFloat("Minimum average cost", ref mMinimumAverageCost, 0.005f, 0.01f, "%f", minAverageFlags);

            bool training = mTrainer!.IsRunning;
            if (training)
            {
                ImGui.BeginDisabled();
            }

            int batchSize = mTrainer.BatchSize;
            if (ImGui.InputInt("Batch size", ref batchSize))
            {
                mTrainer.BatchSize = batchSize;
            }

            float learningRate = mTrainer.LearningRate;
            if (ImGui.InputFloat("Learning rate", ref learningRate))
            {
                mTrainer.LearningRate = learningRate;
            }

            if (training)
            {
                ImGui.EndDisabled();
            }

            if (ImGui.Button(training ? "Stop" : "Start"))
            {
                if (training)
                {
                    mTrainer.Stop();
                }
                else
                {
                    mTrainer.Start(mDataset!, mNetwork!);
                }
            }

            ImGui.SameLine();
            ImGui.InputFloat("##average-absolute-cost", ref mAverageAbsoluteCost, 0f, 0f, "%.10f", ImGuiInputTextFlags.ReadOnly);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Average absolute cost");
            }

            ImGui.End();
        }

        #endregion

        public ShaderLibrary Library => mLibrary!;
        public IRenderer Renderer => mRenderer!;

        private ImGuiController? mImGui;
        private IRenderer? mRenderer;

        private Network? mNetwork;
        private MNISTDatabase? mDataset;

        private ShaderLibrary? mLibrary;
        private int mCurrentFrame;

        private int[]? mSelectedTestImages;
        private DatasetType mSelectedDataset;

        private int mSelectedImage;
        private ITexture? mDisplayedTexture;

        private string mInputString;
        private float[][]? mOutputs;
        private bool mReadBuffer;
        private DispatcherBufferData? mBufferData;
        private int mPassIndex;
        private IFence? mComputeFence;

        private Trainer? mTrainer;
        private float mAverageAbsoluteCost, mMinimumAverageCost;
        private bool mUseMinimumAverageCost;

        private readonly Queue<IDisposable> mExistingSemaphores;
        private readonly List<IDisposable> mSignaledSemaphores;
    }
}