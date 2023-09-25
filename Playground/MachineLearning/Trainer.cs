using CodePlayground.Graphics;
using Optick.NET;
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

    internal struct TrainerState
    {
        public IDataset Data;
        public Network Network;

        public DispatcherBufferData BufferData;
        public bool Initialized, Transition;

        public TrainerBatchData? Batch;
        public int[] ShuffledIndices;
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

            mContext = context;
            mFence = mContext.CreateFence(true);

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

            mFence.Dispose();
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
            var buffers = mState.BufferData;

            int inputCount = buffers.LayerSizes[0];
            int outputCount = buffers.LayerSizes[^1];

            int inputBufferSize = buffers.PassCount * inputCount;
            int outputBufferSize = buffers.PassCount * outputCount * 2;

            var inputStaging = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, inputBufferSize * Marshal.SizeOf<ushort>());
            var outputStaging = mContext.CreateDeviceBuffer(DeviceBufferUsage.Staging, outputBufferSize * Marshal.SizeOf<ushort>());

            commandList.PushStagingObject(inputStaging);
            commandList.PushStagingObject(outputStaging);

            commandList.Begin();
            using (commandList.Context(GPUQueueType.Compute))
            {
                buffers.InputActivationImage.TransitionLayout(commandList, buffers.InputActivationImage.Layout, DeviceImageLayoutName.CopySource);
                buffers.InputActivationImage.CopyToBuffer(commandList, inputStaging, DeviceImageLayoutName.CopySource);
                buffers.InputActivationImage.TransitionLayout(commandList, DeviceImageLayoutName.CopySource, buffers.InputActivationImage.Layout);

                buffers.OutputImage.TransitionLayout(commandList, buffers.OutputImage.Layout, DeviceImageLayoutName.CopySource);
                buffers.OutputImage.CopyToBuffer(commandList, outputStaging, DeviceImageLayoutName.CopySource);
                buffers.OutputImage.TransitionLayout(commandList, DeviceImageLayoutName.CopySource, buffers.OutputImage.Layout);
            }

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
            using var updateEvent = OptickMacros.Event();
            var queue = mContext.Device.GetQueue(CommandQueueFlags.Compute);

            if (!mState.Initialized)
            {
                return;
            }

            bool advanceBatch = mState.Batch is null;
            if (mState.Batch is not null && (wait || mFence.IsSignaled()))
            {
                using var updateBatchEvent = OptickMacros.Event("Check batch");
                queue.ReleaseFence(mFence, wait);

                var batch = mState.Batch.Value;
                var confidences = NetworkDispatcher.GetConfidenceValues(mState.BufferData);

                int outputCount = mState.Network.LayerSizes[^1];
                float averageAbsoluteCost = 0f;
                for (int i = 0; i < confidences.Length; i++)
                {
                    for (int j = 0; j < outputCount; j++)
                    {
                        float x = confidences[i][j];
                        float y = batch.Expected[i][j];

                        float cost = Cost(x, y);
                        averageAbsoluteCost += MathF.Abs(cost) / (outputCount * confidences.Length);
                    }
                }

                OnBatchResults?.Invoke(new TrainerBatchResults
                {
                    ConfidenceValues = confidences,
                    AverageAbsoluteCost = averageAbsoluteCost,
                    ImageIndices = batch.ImageIndices
                });

                advanceBatch = true;
            }
            else if (wait)
            {
                throw new InvalidOperationException("Fence was not submitted to the queue!");
            }

            if (mRunning && advanceBatch)
            {
                using var advanceBatchEvent = OptickMacros.Event("Advance batch");

                int newBatchIndex = (mState.Batch?.BatchIndex ?? -1) + 1;
                if (newBatchIndex >= BatchCount)
                {
                    newBatchIndex = 0;

                    var indices = new int[mState.Data.Count];
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

                    inputs[i] = mState.Data.GetInput(index);
                    expectedOutputs[i] = mState.Data.GetExpectedOutput(index);
                    imageIndices[i] = index;
                }

                mFence.Reset();
                mState.Batch = new TrainerBatchData
                {
                    BatchIndex = newBatchIndex,
                    ImageIndices = imageIndices,
                    Expected = expectedOutputs
                };

                var commandList = queue.Release();
                commandList.Begin();

                using (commandList.Context(GPUQueueType.Compute))
                {
                    if (mState.Transition)
                    {
                        NetworkDispatcher.TransitionImages(commandList, mState.BufferData);
                        mState.Transition = false;
                    }

                    NetworkDispatcher.ForwardPropagation(commandList, mState.BufferData, inputs);
                    commandList.ExecutionBarrier();
                    NetworkDispatcher.BackPropagation(commandList, mState.BufferData, expectedOutputs);
                    commandList.ExecutionBarrier();
                    NetworkDispatcher.DeltaComposition(commandList, mState.BufferData);
                }

                commandList.End();
                queue.Submit(commandList, fence: mFence);
            }

            if (!mRunning && mState.Initialized)
            {
                using var disposeBuffersEvent = OptickMacros.Event("Dispose buffers");

                // we dont want to trip any validation layers
                queue.ReleaseFence(mFence, true);

                // update network & dispose buffers
                mState.Network.UpdateNetwork(mState.BufferData.DataBuffer, mState.BufferData.DataStride, mState.BufferData.DataOffset);
                mState.BufferData.Dispose();

                // reset flags
                mState.Batch = null;
                mState.Initialized = false;
            }
        }

        public bool IsRunning => mRunning;

        public int BatchCount
        {
            get
            {
                int datasetCount = mState.Data.Count;
                return (datasetCount - (datasetCount % mBatchSize)) / mBatchSize;
            }
        }

        public int BatchSize
        {
            get => mBatchSize;
            set
            {
                if (mRunning)
                {
                    throw new InvalidOperationException("Network is training!");
                }

                mBatchSize = value;
            }
        }

        public float LearningRate
        {
            get => mLearningRate;
            set
            {
                if (mRunning)
                {
                    throw new InvalidOperationException("Network is training!");
                }

                mLearningRate = value;
            }
        }

        public void Start(IDataset dataset, Network network)
        {
            using var startEvent = OptickMacros.Event();
            if (mRunning)
            {
                return;
            }

            if (mState.Initialized)
            {
                Update(true);
            }

            if (mBatchSize > dataset.Count)
            {
                throw new InvalidOperationException("Batch size is greater than dataset size!");
            }

            var indices = new int[dataset.Count];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = i;
            }

            Shuffle(indices);
            mState = new TrainerState
            {
                Data = dataset,
                Network = network,

                BufferData = NetworkDispatcher.CreateBuffers(network, mBatchSize, mLearningRate),
                Initialized = true,
                Transition = true,

                Batch = null,
                ShuffledIndices = indices
            };

            mRunning = true;
        }

        public void Stop()
        {
            // remove? probably adds more overhead than its worth
            using var stopEvent = OptickMacros.Event();
            if (!mRunning)
            {
                return;
            }

            mRunning = false;
        }

        private readonly IGraphicsContext mContext;
        private readonly IFence mFence;

        private TrainerState mState;
        private int mBatchSize;
        private float mLearningRate;
        private bool mDisposed, mRunning;

        private readonly Random mRNG;
    }
}