using CodePlayground.Graphics;
using MachineLearning.Shaders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace MachineLearning
{
    public struct ForwardPropagationBufferData
    {
        public IDeviceBuffer ActivationBuffer, PreSigmoidBuffer, SizeBuffer, DataBuffer;
        public int ActivationStride, ActivationOffset;
        public int PassCount;
    }

    public struct BackPropagationBufferData
    {
        public IDeviceBuffer DeltaBuffer;
        public int DeltaStride, DeltaOffset;
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

        public static ForwardPropagationBufferData ForwardPropagation(ICommandList commandList, Network network, float[][] inputs)
        {
            var layerSizes = network.LayerSizes;
            int inputCount = layerSizes[0];

            int neuronTotal = layerSizes.Aggregate((a, b) => a + b);
            int passCount = inputs.Length;

            if (passCount > mMaxConcurrentPasses)
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

            var reflectionView = pipeline.ReflectionView;
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

            activationBuffer.Map(data =>
            {
                for (int i = 0; i < passCount; i++)
                {
                    var layerActivations = inputs[i];
                    if (layerActivations.Length != inputCount)
                    {
                        throw new ArgumentException("Input size mismatch!");
                    }

                    for (int j = 0; j < inputCount; j++)
                    {
                        int offset = (i * neuronTotal + j) * activationStride + activationOffset;
                        BitConverter.GetBytes(layerActivations[j]).CopyTo(data[offset..]);
                    }
                }
            });

            network.CreateBuffers(mContext, reflectionView, out IDeviceBuffer dataBuffer, out IDeviceBuffer sizeBuffer);
            pipeline.Bind(sizeBuffer, ShaderResources.SizeBufferName);
            pipeline.Bind(dataBuffer, ShaderResources.DataBufferName);
            pipeline.Bind(activationBuffer, ShaderResources.ActivationBufferName);
            pipeline.Bind(preSigmoidBuffer, ShaderResources.PreSigmoidBufferName);

            commandList.PushStagingObject(pipeline);
            pipeline.Bind(commandList, 0);

            mRenderer.DispatchCompute(commandList, passCount, 1, 1);
            return new ForwardPropagationBufferData
            {
                ActivationBuffer = activationBuffer,
                PreSigmoidBuffer = preSigmoidBuffer,
                SizeBuffer = sizeBuffer,
                DataBuffer = dataBuffer,

                ActivationOffset = activationOffset,
                ActivationStride = activationStride,

                PassCount = passCount
            };
        }

        public static BackPropagationBufferData BackPropagation(ICommandList commandList, ForwardPropagationBufferData data, float[][] expected, IReadOnlyList<int> layerSizes)
        {
            int outputCount = layerSizes[^1];
            int passCount = expected.Length;

            if (passCount != data.PassCount)
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

            // going to assume the data buffer has the same stride as the delta buffer
            var reflectionView = pipeline.ReflectionView;
            int startOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, $"{nameof(NetworkDataBuffer.Data)}[0]");
            int endOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, $"{nameof(NetworkDataBuffer.Data)}[1]");
            int stride = startOffset - endOffset;

            int bufferSize = outputCount * stride + startOffset + data.DataBuffer.Size;
            var deltaBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, bufferSize);

            deltaBuffer.Map(data =>
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
                        int offset = bufferIndex * stride + startOffset;

                        float expectedValue = passExpected[j];
                        BitConverter.GetBytes(expectedValue).CopyTo(data[offset..]);
                    }
                }
            });

            pipeline.Bind(data.SizeBuffer, ShaderResources.SizeBufferName);
            pipeline.Bind(data.DataBuffer, ShaderResources.DataBufferName);
            pipeline.Bind(data.ActivationBuffer, ShaderResources.ActivationBufferName);
            pipeline.Bind(data.PreSigmoidBuffer, ShaderResources.PreSigmoidBufferName);
            pipeline.Bind(deltaBuffer, ShaderResources.DeltaBufferName);

            commandList.PushStagingObject(pipeline);
            pipeline.Bind(commandList, 0);

            mRenderer.DispatchCompute(commandList, passCount, 1, 1);
            return new BackPropagationBufferData
            {
                DeltaBuffer = deltaBuffer,
                DeltaStride = stride,
                DeltaOffset = startOffset
            };
        }

        public static float[][] GetConfidenceValues(IDeviceBuffer activations, int stride, int dataOffset, int passCount, IReadOnlyList<int> layerSizes)
        {
            int layerCount = layerSizes.Count;
            int confidenceCount = layerSizes[^1];

            int neuronTotal = layerSizes.Aggregate((a, b) => a + b);
            int layerOffset = neuronTotal - confidenceCount;

            var results = new float[passCount][];
            activations.Map(data =>
            {
                var floatSpan = MemoryMarshal.Cast<byte, float>(data);
                for (int i = 0; i < passCount; i++)
                {
                    var passConfidences = new float[confidenceCount];
                    int currentLayerOffset = neuronTotal * i + layerOffset;

                    for (int j = 0; j < confidenceCount; j++)
                    {
                        int offset = (currentLayerOffset + j) * stride + dataOffset;
                        var slice = data[offset..(offset + Marshal.SizeOf<float>())];

                        passConfidences[j] = BitConverter.ToSingle(slice);
                    }

                    results[i] = passConfidences;
                }
            });

            return results;
        }
    }
}
