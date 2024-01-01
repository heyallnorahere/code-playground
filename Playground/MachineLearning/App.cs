using CodePlayground;
using CodePlayground.Graphics;
using ImGuiNET;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MachineLearning
{
    internal struct DatasetSource
    {
        public string Images;
        public string Labels;
    }

    internal struct DatasetImageSampler : ISamplerSettings
    {
        public AddressMode AddressMode => AddressMode.Repeat;
        public SamplerFilter Filter => SamplerFilter.Nearest;
    }

    internal struct TrainingDiagnosticData
    {
        public ITexture Texture;
        public float[] Outputs, ExpectedOutputs;
    }

    [ApplicationTitle("Machine learning test")]
    internal sealed class App : GraphicsApplication
    {
        public static int Main(string[] args) => RunApplication<App>(args);

        public static new App Instance => (App)Application.Instance;
        public static Random RNG => sRandom;

        private static readonly Random sRandom;
        private static readonly IReadOnlyDictionary<DatasetGroup, DatasetSource> sDatasetSources;
        static App()
        {
            sRandom = new Random();
            sDatasetSources = new Dictionary<DatasetGroup, DatasetSource>
            {
                [DatasetGroup.Training] = new DatasetSource
                {
                    Images = "http://yann.lecun.com/exdb/mnist/train-images-idx3-ubyte.gz",
                    Labels = "http://yann.lecun.com/exdb/mnist/train-labels-idx1-ubyte.gz"
                },
                [DatasetGroup.Testing] = new DatasetSource
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
            mSelectedDataset = DatasetGroup.Testing;

            mAverageAbsoluteCost = float.PositiveInfinity;
            mMinimumAverageCost = -1f;

            Load += OnLoad;
            InputReady += OnInputReady;
            Closing += OnClose;

            Update += OnUpdate;
            Render += OnRender;
        }

        public override bool ShouldRunHeadless => mHeadless;
        protected override void ParseArguments()
        {
            var args = CommandLineArguments;
            if (args.Length == 0)
            {
                mHeadless = false;
                return;
            }

            if (args[0] != "train-headless")
            {
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
                mSelectedDataset = Enum.Parse<DatasetGroup>(selectedDataString[..separatorPosition], true);
            }
            else
            {
                mMinimumAverageCost = float.Parse(args[1]);
            }

            mHeadless = true;
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
            Console.WriteLine(mAverageAbsoluteCost = results.AverageAbsoluteCost);
        }

        private static Network InitializeNetwork(IDataset dataset)
        {
            var network = new Network(new int[]
            {
                    dataset.InputCount, // input
                    64, // arbitrary hidden layer sizes
                    16,
                    dataset.OutputCount
            });

            /*
            network.SetActivationFunctions(new ActivationFunction[]
            {
                ActivationFunction.LeakyReLU,
                ActivationFunction.LeakyReLU,
                ActivationFunction.NormalizedHyperbolicTangent
            });*/

            return network;
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

            InitializeImGui();

            mLibrary = new ShaderLibrary(context, Assembly.GetExecutingAssembly());
            mRenderer = context.CreateRenderer();
            NetworkDispatcher.Initialize(mRenderer, mLibrary);

            mTrainer = new Trainer(context, 100, 0.1f); // initial batch size and learning rate
            mTrainer.MinimumAverageCost = mMinimumAverageCost;
            mTrainer.OnBatchResults += OnBatchResults;

            mDataset = new MNISTDatabase();
            foreach (var group in sDatasetSources.Keys)
            {
                var source = sDatasetSources[group];
                var cachePath = $"data/{group.ToString().ToLower()}/";

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

                var data = MNISTGroup.Load(imageSource, labelSource);
                mDataset.SetGroup(group, data);
            }

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
                mNetwork = InitializeNetwork(mDataset);
            }

            if (!mHeadless)
            {
                const int diagnosticImageCount = 5;
                mDiagnosticData = new TrainingDiagnosticData[diagnosticImageCount];

                int outputCount = mNetwork.LayerSizes[^1];
                for (int i = 0; i < mDiagnosticData.Length; i++)
                {
                    mDiagnosticData[i] = new TrainingDiagnosticData
                    {
                        Texture = context.CreateDeviceImage(new DeviceImageInfo
                        {
                            Size = new Size(mDataset.Width, mDataset.Height),
                            Usage = DeviceImageUsageFlags.CopyDestination | DeviceImageUsageFlags.Render,
                            Format = DeviceImageFormat.RGBA8_UNORM,
                            MipLevels = 1
                        }).CreateTexture(true, new DatasetImageSampler()),

                        Outputs = new float[outputCount],
                        ExpectedOutputs = new float[outputCount]
                    };
                }
            }

            var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            var image = mDisplayedTexture.Image;
            var layout = image.GetLayout(DeviceImageLayoutName.ShaderReadOnly);

            image.TransitionLayout(commandList, image.Layout, layout);
            image.Layout = layout;

            if (mDiagnosticData is not null)
            {
                for (int i = 0; i < mDiagnosticData.Length; i++)
                {
                    image = mDiagnosticData[i].Texture.Image;
                    image.TransitionLayout(commandList, image.Layout, layout);
                    image.Layout = layout;
                }
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
            var view = RootView;
            var renderTarget = graphicsContext?.Swapchain?.RenderTarget;

            if (mImGui is not null || view is null || graphicsContext is null || inputContext is null || renderTarget is null)
            {
                return;
            }

            var queue = graphicsContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();

            commandList.Begin();
            mImGui = new ImGuiController(graphicsContext, inputContext, view, renderTarget, SynchronizationFrames);
            mImGui.LoadFontAtlas(commandList);

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

            if (mDiagnosticData is not null)
            {
                foreach (var data in mDiagnosticData)
                {
                    data.Texture.Dispose();
                }
            }

            mImGui?.Dispose();
            mDisplayedTexture?.Dispose();
            mComputeFence?.Dispose();
            mBufferData?.Dispose();
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
            var inputs = imageNumbers.Select(number => mDataset!.GetInput(mSelectedDataset, number - 1)).ToArray();

            var context = GraphicsContext!;
            var queue = context.Device.GetQueue(CommandQueueFlags.Compute);

            var commandList = queue.Release();
            commandList.Begin();

            using var data = NetworkDispatcher.CreateBuffers(mNetwork!, inputs.Length);
            NetworkDispatcher.TransitionImages(commandList, data);
            NetworkDispatcher.ForwardPropagation(commandList, data, inputs);

            commandList.End();
            queue.Submit(commandList);
            queue.ClearCache();

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

        private void OnRender(FrameRenderInfo renderInfo)
        {
            if (mHeadless)
            {
                if (mSelectedTestImages is not null)
                {
                    RunNeuralNetwork(mSelectedTestImages);
                }
                else if (mTrainer is not null)
                {
                    mTrainer.Start(mDataset!, mNetwork!);
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Create("ios")) && !RuntimeInformation.IsOSPlatform(OSPlatform.Create("maccatalyst")))
                    {
                        Console.CancelKeyPress += (sender, args) =>
                        {
                            Console.CancelKeyPress += (sender, args) =>
                            {
                                mTrainer.Stop();
                                args.Cancel = true;
                            };
                        };
                    }

                    int n = 0;
                    do
                    {
                        if (mTrainer.IsRunning)
                        {
                            n++;
                        }

                        mTrainer.Update(true);
                    } while (--n >= 0);
                }

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

            if (ImGui.BeginCombo("##dataset-group", mSelectedDataset.ToString()))
            {
                var types = Enum.GetValues<DatasetGroup>();
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

            ImGui.InputInt("##selected-image", ref mSelectedImage);
            ImGui.SameLine();

            if (ImGui.Button("Load image"))
            {
                int imageCount = mDataset!.GetGroupEntryCount(mSelectedDataset);
                if (mSelectedImage <= 0 || mSelectedImage > imageCount)
                {
                    throw new IndexOutOfRangeException();
                }

                int imageIndex = mSelectedImage - 1;
                var imageData = mDataset!.GetImageData(mSelectedDataset, imageIndex, 4); // rgba

                var context = GraphicsContext!;
                var buffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, imageData.Length);
                buffer.CopyFromCPU(imageData);

                var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);
                var commandList = queue.Release();

                commandList.Begin();
                var image = mDisplayedTexture!.Image;
                image.CopyFromBuffer(commandList, buffer, image.Layout);

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
                    if (number <= 0 || number > mDataset.GetGroupEntryCount(mSelectedDataset))
                    {
                        throw new ArgumentException("Invalid image number!");
                    }

                    int imageIndex = number - 1;
                    inputs[i] = mDataset.GetInput(mSelectedDataset, imageIndex);
                }

                var context = GraphicsContext!;
                var queue = context.Device.GetQueue(CommandQueueFlags.Compute);
                var commandList = queue.Release();

                commandList.Begin();
                var data = NetworkDispatcher.CreateBuffers(mNetwork!, inputs.Length);
                NetworkDispatcher.TransitionImages(commandList, data);
                NetworkDispatcher.ForwardPropagation(commandList, data, inputs);

                mBufferData?.Dispose();
                mBufferData = data;

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

            bool training = mTrainer!.IsRunning;
            if (training)
            {
                ImGui.BeginDisabled();
            }

            float minimumAverageCost = mTrainer.MinimumAverageCost;
            if (ImGui.InputFloat("Minimum average cost", ref minimumAverageCost, 0.005f, 0.01f, "%f"))
            {
                mTrainer.MinimumAverageCost = minimumAverageCost;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("""
                    The threshold to verify against after every training/evaluation run.
                    A negative value will cause training to never terminate.
                    """);
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
                    if (mDiagnosticData is not null)
                    {
                        var interpretedData = mTrainer.RetrieveInterpretedData();

                        var context = GraphicsContext!;
                        var queue = context.Device.GetQueue(CommandQueueFlags.Transfer);

                        var commandList = queue.Release();
                        SignalSemaphore(commandList);

                        commandList.Begin();
                        int width = mDataset!.Width;
                        int height = mDataset!.Height;

                        for (int i = 0; i < mDiagnosticData.Length; i++)
                        {
                            var diagnosticData = mDiagnosticData[i];
                            var data = i < interpretedData.Length ? interpretedData[^(i + 1)] : new InterpretedPassDetails
                            {
                                Inputs = new float[width * height],
                                Outputs = new float[diagnosticData.Outputs.Length],
                                ExpectedOutputs = new float[diagnosticData.ExpectedOutputs.Length]
                            };

                            data.Outputs.CopyTo(diagnosticData.Outputs, 0);
                            data.ExpectedOutputs.CopyTo(diagnosticData.ExpectedOutputs, 0);

                            const int channels = 4;
                            var inputData = data.Inputs;
                            var imageData = new byte[inputData.Length * channels];

                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    int pixel = y * width + x;
                                    int pixelOffset = pixel * channels;

                                    for (int j = 0; j < channels; j++)
                                    {
                                        imageData[pixelOffset + j] = j < 3 ? (byte)(inputData[pixel] * byte.MaxValue) : byte.MaxValue;
                                    }
                                }
                            }

                            var stagingBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Staging, imageData.Length);
                            var image = mDiagnosticData[i].Texture.Image;

                            commandList.PushStagingObject(stagingBuffer);
                            stagingBuffer.CopyFromCPU(imageData);
                            image.TransitionLayout(commandList, image.Layout, DeviceImageLayoutName.CopyDestination);
                            image.CopyFromBuffer(commandList, stagingBuffer, DeviceImageLayoutName.CopyDestination);
                            image.TransitionLayout(commandList, DeviceImageLayoutName.CopyDestination, image.Layout);
                        }

                        commandList.End();
                        queue.Submit(commandList);
                    }

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

            if (training)
            {
                ImGui.BeginDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button("Reset network"))
            {
                mNetwork = InitializeNetwork(mDataset!);
            }

            if (training)
            {
                ImGui.EndDisabled();
            }

            if (mDiagnosticData is not null)
            {
                ImGui.Columns(mDiagnosticData.Length, "diagnostic-data", false);
                for (int i = 0; i < mDiagnosticData.Length; i++)
                {
                    var data = mDiagnosticData[i];
                    nint id = mImGui!.GetTextureID(data.Texture);

                    float width = ImGui.GetColumnWidth();
                    ImGui.Image(id, new Vector2(width));

                    for (int j = 0; j < data.Outputs.Length; j++)
                    {
                        ImGui.TextUnformatted($"{data.Outputs[j]:0.##} : {data.ExpectedOutputs[j]:0.##}");
                    }

                    ImGui.NextColumn();
                }

                ImGui.Columns(1);
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

        private bool mHeadless;
        private int[]? mSelectedTestImages;
        private DatasetGroup mSelectedDataset;

        private int mSelectedImage;
        private ITexture? mDisplayedTexture;

        private string mInputString;
        private float[][]? mOutputs;
        private bool mReadBuffer;
        private DispatcherBufferData? mBufferData;
        private int mPassIndex;
        private IFence? mComputeFence;
        private TrainingDiagnosticData[]? mDiagnosticData;

        private Trainer? mTrainer;
        private float mAverageAbsoluteCost, mMinimumAverageCost;

        private readonly Queue<IDisposable> mExistingSemaphores;
        private readonly List<IDisposable> mSignaledSemaphores;
    }
}