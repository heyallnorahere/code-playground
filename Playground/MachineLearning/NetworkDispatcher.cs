using CodePlayground.Graphics;
using MachineLearning.Shaders;
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace MachineLearning
{
    public struct DispatcherBufferData
    {
        public IDeviceBuffer ActivationBuffer, PreSigmoidBuffer, SizeBuffer, DataBuffer, DeltaBuffer;
        public int ActivationOffset, DeltaOffset, DataOffset;
        public int ActivationStride, DeltaStride, DataStride;
        public int PassCount;
        public int[] LayerSizes;
    }

    public static class NetworkDispatcher
    {
        private static readonly ShaderLibrary mLibrary;
        private static readonly IRenderer mRenderer;
        private static readonly IGraphicsContext mContext;
        private static readonly int mMaxConcurrentPasses;

        public static int MaxConcurrentPasses => mMaxConcurrentPasses;

        static NetworkDispatcher()
        {
            var app = App.Instance;

            mRenderer = app.Renderer;
            mLibrary = app.Library;
            mContext = mLibrary.Context;

            var deviceInfo = mContext.Device.DeviceInfo;
            mMaxConcurrentPasses = (int)deviceInfo.MaxComputeWorkGroups.X;
        }

        public static DispatcherBufferData CreateBuffers(Network network, int passCount)
        {
            var reflectionView = mLibrary.CreateReflectionView<BackPropagation>(); // backprop shader accesses all resources

            var layerSizes = network.LayerSizes;
            int inputCount = layerSizes[0];
            int outputCount = layerSizes[^1];
            int neuronTotal = layerSizes.Aggregate((a, b) => a + b);

            int activationOffset = reflectionView.GetBufferOffset(ShaderResources.ActivationBufferName, $"{nameof(NetworkDataBuffer.Data)}[0]");
            int endOffset = reflectionView.GetBufferOffset(ShaderResources.ActivationBufferName, $"{nameof(NetworkDataBuffer.Data)}[1]");
            int activationStride = endOffset - activationOffset;

            int preSigmoidOffset = reflectionView.GetBufferOffset(ShaderResources.PreSigmoidBufferName, $"{nameof(NetworkDataBuffer.Data)}[0]");
            endOffset = reflectionView.GetBufferOffset(ShaderResources.PreSigmoidBufferName, $"{nameof(NetworkDataBuffer.Data)}[1]");
            int preSigmoidStride = endOffset - preSigmoidOffset;

            int activationCount = neuronTotal * passCount;
            int activationBufferSize = activationStride * activationCount + activationOffset;

            int preSigmoidalValueCount = (neuronTotal - inputCount) * passCount;
            int preSigmoidBufferSize = preSigmoidalValueCount * preSigmoidStride + preSigmoidOffset;

            var activationBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, activationBufferSize);
            var preSigmoidBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, preSigmoidBufferSize);

            network.CreateBuffers(mContext, reflectionView, out IDeviceBuffer dataBuffer, out IDeviceBuffer sizeBuffer, out int dataStride, out int dataOffset);

            int deltaOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, $"{nameof(NetworkDataBuffer.Data)}[0]");
            endOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, $"{nameof(NetworkDataBuffer.Data)}[1]");
            int deltaStride = deltaOffset - endOffset;

            // going to assume the data buffer has the same stride as the delta buffer
            int deltaBufferSize = outputCount * deltaStride + deltaOffset + dataBuffer.Size - dataOffset;
            var deltaBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, deltaBufferSize);

            return new DispatcherBufferData
            {
                ActivationBuffer = activationBuffer,
                PreSigmoidBuffer = preSigmoidBuffer,
                SizeBuffer = sizeBuffer,
                DataBuffer = dataBuffer,
                DeltaBuffer = deltaBuffer,

                ActivationOffset = activationOffset,
                DeltaOffset = deltaOffset,
                DataOffset = dataOffset,

                ActivationStride = activationStride,
                DeltaStride = deltaStride,
                DataStride = dataStride,

                PassCount = passCount,
                LayerSizes = layerSizes.ToArray()
            };
        }

        public static void ForwardPropagation(ICommandList commandList, DispatcherBufferData buffers, float[][] inputs)
        {
            int inputCount = buffers.LayerSizes[0];
            int neuronTotal = buffers.LayerSizes.Aggregate((a, b) => a + b);

            if (inputs.Length != buffers.PassCount)
            {
                throw new ArgumentException("Inconsistent pass count!");
            }

            if (buffers.PassCount > mMaxConcurrentPasses)
            {
                throw new ArgumentException($"Cannot execute more than {mMaxConcurrentPasses} passes at a time!");
            }

            var pipeline = mLibrary.LoadPipeline<ForwardPropagation>(new PipelineDescription
            {
                RenderTarget = null,
                Type = PipelineType.Compute,
                FrameCount = 1,
                Specification = null
            });

            buffers.ActivationBuffer.Map(data =>
            {
                for (int i = 0; i < buffers.PassCount; i++)
                {
                    var layerActivations = inputs[i];
                    if (layerActivations.Length != inputCount)
                    {
                        throw new ArgumentException("Input size mismatch!");
                    }

                    for (int j = 0; j < inputCount; j++)
                    {
                        int offset = (i * neuronTotal + j) * buffers.ActivationStride + buffers.ActivationOffset;
                        BitConverter.GetBytes(layerActivations[j]).CopyTo(data[offset..]);
                    }
                }
            });

            pipeline.Bind(buffers.SizeBuffer, ShaderResources.SizeBufferName);
            pipeline.Bind(buffers.DataBuffer, ShaderResources.DataBufferName);
            pipeline.Bind(buffers.ActivationBuffer, ShaderResources.ActivationBufferName);
            pipeline.Bind(buffers.PreSigmoidBuffer, ShaderResources.PreSigmoidBufferName);

            commandList.PushStagingObject(pipeline);
            pipeline.Bind(commandList, 0);

            mRenderer.DispatchCompute(commandList, buffers.PassCount, 1, 1);
        }

        public static void BackPropagation(ICommandList commandList, DispatcherBufferData buffers, float[][] expected)
        {
            int outputCount = buffers.LayerSizes[^1];
            int passCount = expected.Length;

            if (expected.Length != buffers.PassCount)
            {
                throw new ArgumentException("Inconsistent pass count!");
            }

            var pipeline = mLibrary.LoadPipeline<BackPropagation>(new PipelineDescription
            {
                RenderTarget = null,
                Type = PipelineType.Compute,
                FrameCount = 1,
                Specification = null
            });

            buffers.DeltaBuffer.Map(data =>
            {
                for (int i = 0; i < passCount; i++)
                {
                    var passExpected = expected[i];
                    if (passExpected.Length != outputCount)
                    {
                        throw new ArgumentException("Inconsistent pass count!");
                    }

                    for (int j = 0; j < outputCount; j++)
                    {
                        int bufferIndex = i * outputCount + j;
                        int offset = bufferIndex * buffers.DeltaStride + buffers.DeltaOffset;

                        float expectedValue = passExpected[j];
                        BitConverter.GetBytes(expectedValue).CopyTo(data[offset..]);
                    }
                }
            });

            pipeline.Bind(buffers.SizeBuffer, ShaderResources.SizeBufferName);
            pipeline.Bind(buffers.DataBuffer, ShaderResources.DataBufferName);
            pipeline.Bind(buffers.ActivationBuffer, ShaderResources.ActivationBufferName);
            pipeline.Bind(buffers.PreSigmoidBuffer, ShaderResources.PreSigmoidBufferName);
            pipeline.Bind(buffers.DeltaBuffer, ShaderResources.DeltaBufferName);

            commandList.PushStagingObject(pipeline);
            pipeline.Bind(commandList, 0);

            mRenderer.DispatchCompute(commandList, passCount, 1, 1);
        }

        public static float[][] GetConfidenceValues(DispatcherBufferData buffers)
        {
            int layerCount = buffers.LayerSizes.Length;
            int confidenceCount = buffers.LayerSizes[^1];

            int neuronTotal = buffers.LayerSizes.Aggregate((a, b) => a + b);
            int layerOffset = neuronTotal - confidenceCount;

            var results = new float[buffers.PassCount][];
            buffers.ActivationBuffer.Map(data =>
            {
                var floatSpan = MemoryMarshal.Cast<byte, float>(data);
                for (int i = 0; i < buffers.PassCount; i++)
                {
                    var passConfidences = new float[confidenceCount];
                    int currentLayerOffset = neuronTotal * i + layerOffset;

                    for (int j = 0; j < confidenceCount; j++)
                    {
                        int offset = (currentLayerOffset + j) * buffers.ActivationStride + buffers.ActivationOffset;
                        var slice = data[offset..(offset + Marshal.SizeOf<float>())];

                        passConfidences[j] = BitConverter.ToSingle(slice);
                    }

                    results[i] = passConfidences;
                }
            });

            return results;
        }

        public static Layer[] GetDeltas(DispatcherBufferData buffers)
        {
            int dataMatrixSize = buffers.DataBuffer.Size - buffers.DataOffset;
            int deltaMatrixSize = dataMatrixSize * buffers.DeltaStride / buffers.DataStride;
            int deltaMatrixOffset = buffers.DeltaBuffer.Size - deltaMatrixSize;

            var deltas = new Layer[buffers.LayerSizes.Length - 1];
            buffers.DeltaBuffer.Map(data =>
            {
                for (int i = 0; i < deltas.Length; i++)
                {
                    int currentLayerSize = buffers.LayerSizes[i + 1];
                    int previousLayerSize = buffers.LayerSizes[i];

                    var delta = new Layer(currentLayerSize, previousLayerSize);
                    int matrixRowLength = previousLayerSize + 1;

                    for (int j = 0; j < buffers.PassCount; j++)
                    {
                        int matrixOffset = deltaMatrixOffset + deltaMatrixSize * j;
                        for (int y = 0; y < currentLayerSize; y++)
                        {
                            int matrixRowOffset = matrixRowLength * y;
                            int biasDeltaOffset = matrixOffset + matrixRowOffset * buffers.DeltaStride;
                            float biasDelta = BitConverter.ToSingle(data[biasDeltaOffset..]);

                            delta.Biases[y] += biasDelta / buffers.PassCount;
                            for (int x = 0; x < previousLayerSize; x++)
                            {
                                int matrixColumnOffset = matrixRowOffset + x + 1;
                                int weightDeltaOffset = matrixOffset + matrixColumnOffset * buffers.DeltaStride;
                                float weightDelta = BitConverter.ToSingle(data[weightDeltaOffset..]);

                                delta.Weights[y, x] += weightDelta / buffers.PassCount;
                            }
                        }
                    }

                    deltas[i] = delta;
                }
            });

            return deltas;
        }
    }
}
