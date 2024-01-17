using CodePlayground;
using CodePlayground.Graphics;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;

namespace MachineLearning
{
    public struct TrainerBatchResults
    {
        public float[][] ConfidenceValues;
        public float AverageAbsoluteCost;
        public int[] ImageIndices;
    }

    internal struct TrainerBatchData
    {
        public int BatchIndex;
        public int[] ImageIndices;
        public float[][] Expected;
    }

    internal struct TrainerFrameData
    {
        public DispatcherBufferData BufferData;
        public IFence Fence;
        public bool Transition;
    }

    internal struct TrainerState
    {
        public IDataset Data;
        public Network Network;

        public int CurrentFrame;
        public TrainerFrameData[] Frames;
        public bool Initialized;

        public TrainerBatchData? Batch;
        public int[] ShuffledIndices;
        public DatasetGroup Phase;
        public float AverageAbsoluteCost;
    }

    public struct InterpretedPassDetails
    {
        public float[] Inputs;
        public float[] Outputs;
        public float[] ExpectedOutputs;
    }

    public sealed class Trainer : IDisposable
    {
        public static float Cost(float x, float y) => MathF.Pow(x - y, 2f);

        public Trainer(IGraphicsContext context, int batchSize, float learningRate)
        {
            mDisposed = false;
            mRunning = false;

            mBatchSize = batchSize;
            mLearningRate = learningRate;
            mMinimumAverageCost = -1f;

            mContext = context;
            mRNG = new Random();
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            if (mRunning)
            {
                Stop();
            }

            if (mState.Initialized)
            {
                Update(true);
            }

            mDisposed = true;
        }

        public void Shuffle<T>(T[] array)
        {
            int n = array.Length;
            while (n > 1)
            {
                int i = mRNG.Next(n--);
                (array[i], array[n]) = (array[n], array[i]);
            }
        }

        public InterpretedPassDetails[] RetrieveInterpretedData()
        {
            var queue = mContext.Device.GetQueue(CommandQueueFlags.Compute);
            var commandList = queue.Release();

            int previousFrame = (mState.CurrentFrame > 0 ? mState.CurrentFrame : mState.Frames.Length) - 1;
            var buffers = mState.Frames[previousFrame].BufferData;

            int inputCount = buffers.LayerSizes[0];
            int outputCount = buffers.LayerSizes[^1];

            int inputBufferSize = buffers.PassCount * inputCount;
            int outputBufferSize = buffers.PassCount * outputCount * 2;

            var inputStaging = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, inputBufferSize * Marshal.SizeOf<ushort>());
            var outputStaging = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, outputBufferSize * Marshal.SizeOf<ushort>());

            commandList.PushStagingObject(inputStaging);
            commandList.PushStagingObject(outputStaging);

            commandList.Begin();
            buffers.InputActivationImage.TransitionLayout(commandList, buffers.InputActivationImage.Layout, DeviceImageLayoutName.CopySource);
            buffers.InputActivationImage.CopyToBuffer(commandList, inputStaging, DeviceImageLayoutName.CopySource);
            buffers.InputActivationImage.TransitionLayout(commandList, DeviceImageLayoutName.CopySource, buffers.InputActivationImage.Layout);

            buffers.OutputImage.TransitionLayout(commandList, buffers.OutputImage.Layout, DeviceImageLayoutName.CopySource);
            buffers.OutputImage.CopyToBuffer(commandList, outputStaging, DeviceImageLayoutName.CopySource);
            buffers.OutputImage.TransitionLayout(commandList, DeviceImageLayoutName.CopySource, buffers.OutputImage.Layout);

            commandList.End();
            queue.Submit(commandList, wait: true);

            var inputData = new ushort[inputBufferSize];
            var outputData = new ushort[outputBufferSize];

            inputStaging.CopyToCPU(inputData);
            outputStaging.CopyToCPU(outputData);

            var results = new InterpretedPassDetails[buffers.PassCount];
            for (int i = 0; i < buffers.PassCount; i++)
            {
                var details = new InterpretedPassDetails
                {
                    Inputs = new float[inputCount],
                    Outputs = new float[outputCount],
                    ExpectedOutputs = new float[outputCount]
                };

                for (int j = 0; j < inputCount; j++)
                {
                    int inputIndex = i * inputCount + j;
                    details.Inputs[j] = (float)inputData[inputIndex] / ushort.MaxValue;
                }

                for (int j = 0; j < outputCount; j++)
                {
                    int outputOffset = (i * outputCount + j) * 2;
                    details.ExpectedOutputs[j] = (float)outputData[outputOffset] / ushort.MaxValue;
                    details.Outputs[j] = (float)outputData[outputOffset + 1] / ushort.MaxValue;
                }

                results[i] = details;
            }

            return results;
        }

        public event Action<TrainerBatchResults>? OnBatchResults;
        public void Update(bool wait = false)
        {
            using var updateEvent = Profiler.Event();
            var queue = mContext.Device.GetQueue(CommandQueueFlags.Compute);

            if (!mState.Initialized)
            {
                return;
            }

            bool advanceBatch = mState.Batch is null;
            var fence = mState.Frames[mState.CurrentFrame].Fence;
            if (mState.Batch is not null && (wait || fence.IsSignaled()))
            {
                using var updateBatchEvent = Profiler.Event("Check batch");
                queue.ReleaseFence(fence, wait);

                var batch = mState.Batch.Value;
                var confidences = NetworkDispatcher.GetConfidenceValues(mState.Frames[mState.CurrentFrame].BufferData);

                int outputCount = mState.Network.LayerSizes[^1];
                float scale = 1f / (outputCount * mBatchSize);

                if (mState.Phase != DatasetGroup.Training)
                {
                    scale /= GetBatchCount(mState.Phase);
                }
                else
                {
                    mState.AverageAbsoluteCost = 0f;
                }

                for (int i = 0; i < confidences.Length; i++)
                {
                    for (int j = 0; j < outputCount; j++)
                    {
                        float x = confidences[i][j];
                        float y = batch.Expected[i][j];

                        float cost = Cost(x, y);
                        mState.AverageAbsoluteCost += MathF.Abs(cost) * scale;
                    }
                }

                if (mState.Phase == DatasetGroup.Training)
                {
                    OnBatchResults?.Invoke(new TrainerBatchResults
                    {
                        ConfidenceValues = confidences,
                        AverageAbsoluteCost = mState.AverageAbsoluteCost,
                        ImageIndices = batch.ImageIndices
                    });
                }

                advanceBatch = true;
            }

            if (mRunning && advanceBatch)
            {
                using var advanceBatchEvent = Profiler.Event("Advance batch");

                int newBatchIndex = (mState.Batch?.BatchIndex ?? -1) + 1;
                mState.CurrentFrame++;
                mState.CurrentFrame %= mState.Frames.Length;

                if (newBatchIndex >= GetBatchCount(mState.Phase))
                {
                    switch (mState.Phase)
                    {
                        case DatasetGroup.Training:
                            if (mState.Data.GetGroupEntryCount(DatasetGroup.Testing) >= 0)
                            {
                                mState.Phase = DatasetGroup.Testing;
                            }
                            else if (mState.AverageAbsoluteCost <= mMinimumAverageCost)
                            {
                                Stop();
                                return;
                            }

                            break;
                        case DatasetGroup.Testing:
                            if (mState.AverageAbsoluteCost <= mMinimumAverageCost)
                            {
                                if (mState.Data.GetGroupEntryCount(DatasetGroup.Evaluation) >= 0)
                                {
                                    mState.Phase = DatasetGroup.Evaluation;
                                }
                                else
                                {
                                    Stop();
                                    return;
                                }
                            }
                            else
                            {
                                mState.Phase = DatasetGroup.Training;
                            }

                            break;
                        case DatasetGroup.Evaluation:
                            if (mState.AverageAbsoluteCost <= mMinimumAverageCost)
                            {
                                Stop();
                                return;
                            }
                            else
                            {
                                mState.Phase = DatasetGroup.Training;
                            }

                            break;
                    }

                    mState.AverageAbsoluteCost = 0f;
                    newBatchIndex = 0;

                    var indices = new int[mState.Data.GetGroupEntryCount(mState.Phase)];
                    for (int i = 0; i < indices.Length; i++)
                    {
                        indices[i] = i;
                    }

                    Shuffle(indices);
                    mState.ShuffledIndices = indices;
                }

                var inputs = new float[mBatchSize][];
                var expectedOutputs = new float[mBatchSize][];
                var imageIndices = new int[mBatchSize];

                for (int i = 0; i < mBatchSize; i++)
                {
                    int index = mState.ShuffledIndices[newBatchIndex * mBatchSize + i];

                    inputs[i] = mState.Data.GetInput(mState.Phase, index);
                    expectedOutputs[i] = mState.Data.GetExpectedOutput(mState.Phase, index);
                    imageIndices[i] = index;
                }

                ref var frame = ref mState.Frames[mState.CurrentFrame];
                fence = frame.Fence;
                fence.Reset();

                mState.Batch = new TrainerBatchData
                {
                    BatchIndex = newBatchIndex,
                    ImageIndices = imageIndices,
                    Expected = expectedOutputs
                };

                var commandList = queue.Release();
                commandList.Begin();

                if (frame.Transition)
                {
                    NetworkDispatcher.TransitionImages(commandList, frame.BufferData);
                    frame.Transition = false;
                }

                NetworkDispatcher.ForwardPropagation(commandList, frame.BufferData, inputs);
                if (mState.Phase == DatasetGroup.Training)
                {
                    commandList.ExecutionBarrier();
                    NetworkDispatcher.BackPropagation(commandList, frame.BufferData, expectedOutputs);
                    commandList.ExecutionBarrier();
                    NetworkDispatcher.DeltaComposition(commandList, frame.BufferData);
                }

                commandList.End();
                queue.Submit(commandList, fence: fence);
            }

            if (!mRunning && mState.Initialized)
            {
                using var disposeBuffersEvent = Profiler.Event("Dispose buffers");

                foreach (var frameData in mState.Frames)
                {
                    // we dont want to trip any validation layers
                    queue.ReleaseFence(frameData.Fence, true);

                    // update network & dispose buffers
                    mState.Network.UpdateNetwork(frameData.BufferData.DataBuffer, frameData.BufferData.DataStride, frameData.BufferData.DataOffset);
                    frameData.BufferData.Dispose();
                    frameData.Fence.Dispose();
                }

                // reset flags
                mState.Batch = null;
                mState.Initialized = false;
            }
        }

        public bool IsRunning => mRunning;

        public int GetBatchCount(DatasetGroup group)
        {
            int datasetCount = mState.Data.GetGroupEntryCount(group);
            return (datasetCount - (datasetCount % mBatchSize)) / mBatchSize;
        }

        private void VerifyStopped()
        {
            if (mRunning)
            {
                throw new InvalidOperationException("Network is training!");
            }
        }

        public int BatchSize
        {
            get => mBatchSize;
            set
            {
                VerifyStopped();
                mBatchSize = value;
            }
        }

        public float LearningRate
        {
            get => mLearningRate;
            set
            {
                VerifyStopped();
                mLearningRate = value;
            }
        }

        public float MinimumAverageCost
        {
            get => mMinimumAverageCost;
            set
            {
                VerifyStopped();
                mMinimumAverageCost = value;
            }
        }

        public void Start(IDataset dataset, Network network)
        {
            using var startEvent = Profiler.Event();
            if (mRunning)
            {
                return;
            }

            if (mState.Initialized)
            {
                Update(true);
            }

            if (dataset.GetGroupEntryCount(DatasetGroup.Testing) < 0)
            {
                Console.WriteLine("[WARNING] no testing group found! it is strongly recommended to include a testing group in your dataset");
            }
            else if (dataset.GetGroupEntryCount(DatasetGroup.Evaluation) < 0)
            {
                Console.WriteLine("[WARNING] no evaluation group found! it is strongly recommended to also include a evaluation group in your dataset");
            }

            var indices = new int[dataset.GetGroupEntryCount(DatasetGroup.Training)];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = i;
            }

            const int frameCount = 3;
            var frames = new TrainerFrameData[frameCount];

            for (int i = 0; i < frameCount; i++)
            {
                frames[i] = new TrainerFrameData
                {
                    BufferData = NetworkDispatcher.CreateBuffers(network, mBatchSize, mLearningRate),
                    Fence = mContext.CreateFence(true),
                    Transition = true
                };
            }

            Shuffle(indices);
            mState = new TrainerState
            {
                Data = dataset,
                Network = network,

                CurrentFrame = 0,
                Frames = frames,
                Initialized = true,

                Batch = null,
                ShuffledIndices = indices,
                Phase = DatasetGroup.Training
            };

            mRunning = true;
        }

        public void Stop()
        {
            // remove? probably adds more overhead than its worth
            using var stopEvent = Profiler.Event();
            if (!mRunning)
            {
                return;
            }

            mRunning = false;
        }

        private readonly IGraphicsContext mContext;

        private TrainerState mState;
        private int mBatchSize;
        private float mLearningRate, mMinimumAverageCost;
        private bool mDisposed, mRunning;

        private readonly Random mRNG;
    }
}