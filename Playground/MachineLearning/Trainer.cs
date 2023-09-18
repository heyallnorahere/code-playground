using CodePlayground.Graphics;
using Optick.NET;
using System;

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
        public bool Initialized;

        public TrainerBatchData? Batch;
        public int[] ShuffledIndices;
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

                var batch = mState.Batch.Value;
                if (queue.ReleaseFence(mFence, wait))
                {
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

                // update network
                mState.Network.UpdateNetwork(mState.BufferData.DataBuffer, mState.BufferData.DataStride, mState.BufferData.DataOffset);

                // dispose buffers
                mState.BufferData.SizeBuffer.Dispose();
                mState.BufferData.DataBuffer.Dispose();
                mState.BufferData.ActivationBuffer.Dispose();
                mState.BufferData.PreSigmoidBuffer.Dispose();
                mState.BufferData.DeltaBuffer.Dispose();

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