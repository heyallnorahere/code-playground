using CodePlayground.Graphics;
using MachineLearning.Shaders;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MachineLearning
{
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

        public static IDeviceBuffer Dispatch(ICommandList commandList, Network network, float[][] inputs, out int bufferStride)
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
            int startOffset = reflectionView.GetBufferOffset(ShaderResources.ActivationBufferName, $"{nameof(NetworkDataBuffer.Data)}[0]");
            int endOffset = reflectionView.GetBufferOffset(ShaderResources.ActivationBufferName, $"{nameof(NetworkDataBuffer.Data)}[1]");
            int stride = endOffset - startOffset;

            int activationCount = neuronTotal * passCount;
            int bufferSize = stride * activationCount + startOffset;

            var activationBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, bufferSize);
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
                        int offset = (i * neuronTotal + j) * stride + startOffset;
                        BitConverter.GetBytes(layerActivations[j]).CopyTo(data[offset..]);
                    }
                }
            });

            network.CreateBuffers(mContext, reflectionView, out IDeviceBuffer dataBuffer, out IDeviceBuffer sizeBuffer);
            pipeline.Bind(sizeBuffer, ShaderResources.SizeBufferName);
            pipeline.Bind(dataBuffer, ShaderResources.DataBufferName);
            pipeline.Bind(activationBuffer, ShaderResources.ActivationBufferName);

            commandList.PushStagingObject(pipeline);
            commandList.PushStagingObject(sizeBuffer);
            commandList.PushStagingObject(dataBuffer);

            pipeline.Bind(commandList, 0);
            mRenderer.DispatchCompute(commandList, passCount, 1, 1);

            bufferStride = stride;
            return activationBuffer;
        }

        public static float[][] GetConfidenceValues(IDeviceBuffer activations, int stride, int passCount, IReadOnlyList<int> layerSizes)
        {
            int layerCount = layerSizes.Count;
            int confidenceCount = layerSizes[^1];

            int neuronTotal = layerSizes.Aggregate((a, b) => a + b);
            int layerOffset = neuronTotal - confidenceCount;

            var results = new float[passCount][];
            activations.Map(data =>
            {
                for (int i = 0; i < passCount; i++)
                {
                    var passConfidences = new float[confidenceCount];
                    int currentLayerOffset = neuronTotal * i + layerOffset;

                    for (int j = 0; j < confidenceCount; j++)
                    {
                        int offset = (currentLayerOffset + j) * stride;
                        passConfidences[j] = BitConverter.ToSingle(data[offset..]);
                    }

                    results[i] = passConfidences;
                }
            });

            return results;
        }
    }
}
