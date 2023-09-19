using CodePlayground.Graphics;
using MachineLearning.Shaders;
using Optick.NET;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;

namespace MachineLearning
{
    public struct DispatcherBufferData
    {
        public IDeviceBuffer ActivationBuffer, PreSigmoidBuffer, SizeBuffer, DataBuffer, DeltaBuffer;
        public int ActivationOffset, DeltaOffset, DataOffset, ActivationFunctionOffset;
        public int ActivationStride, DeltaStride, DataStride, ActivationFunctionStride;
        public int PassCount;
        public int[] LayerSizes;
    }

    public static class NetworkDispatcher
    {
        private static ShaderLibrary? mLibrary;
        private static IRenderer? mRenderer;
        private static IGraphicsContext? mContext;
        private static int mMaxConcurrentPasses;

        public static int MaxConcurrentPasses => mMaxConcurrentPasses;

        private static int GetWorkGroupCount(int layerSize)
        {
            int remainder = layerSize % Network.WorkGroupThreadCount;
            int groupCount = (layerSize - remainder) / Network.WorkGroupThreadCount;

            if (remainder > 0)
            {
                groupCount++;
            }

            return groupCount;
        }

        public static void Initialize(IRenderer renderer, ShaderLibrary library)
        {
            mRenderer = renderer;
            mLibrary = library;
            mContext = mLibrary.Context;

            var deviceInfo = mContext.Device.DeviceInfo;
            mMaxConcurrentPasses = (int)deviceInfo.MaxComputeWorkGroups.X;
        }

        [MemberNotNull(nameof(mRenderer), nameof(mLibrary), nameof(mContext))]
        private static void AssertInitialized()
        {
            if (mRenderer is not null && mLibrary is not null && mContext is not null)
            {
                return;
            }

            throw new InvalidOperationException("The network dispatcher has not been initialized!");
        }

        public static DispatcherBufferData CreateBuffers(Network network, int passCount, float learningRate = 0f)
        {
            AssertInitialized();

            using var createBuffersEvent = OptickMacros.Event();
            var reflectionView = mLibrary.CreateReflectionView<BackPropagation>(); // backprop shader accesses all resources

            var layerSizes = network.LayerSizes;
            int inputCount = layerSizes[0];
            int outputCount = layerSizes[^1];
            int neuronTotal = layerSizes.Aggregate((a, b) => a + b);

            int activationOffset = reflectionView.GetBufferOffset(ShaderResources.ActivationBufferName, $"{nameof(NetworkArrayBuffer.Data)}[0]");
            int endOffset = reflectionView.GetBufferOffset(ShaderResources.ActivationBufferName, $"{nameof(NetworkArrayBuffer.Data)}[1]");
            int activationStride = endOffset - activationOffset;

            int preSigmoidOffset = reflectionView.GetBufferOffset(ShaderResources.PreSigmoidBufferName, $"{nameof(NetworkArrayBuffer.Data)}[0]");
            endOffset = reflectionView.GetBufferOffset(ShaderResources.PreSigmoidBufferName, $"{nameof(NetworkArrayBuffer.Data)}[1]");
            int preSigmoidStride = endOffset - preSigmoidOffset;

            int activationCount = neuronTotal * passCount;
            int activationBufferSize = activationStride * activationCount + activationOffset;

            int preSigmoidalValueCount = (neuronTotal - inputCount) * passCount;
            int preSigmoidBufferSize = preSigmoidalValueCount * preSigmoidStride + preSigmoidOffset;

            var activationBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, activationBufferSize);
            var preSigmoidBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, preSigmoidBufferSize);

            network.CreateBuffers(mContext, reflectionView, out IDeviceBuffer dataBuffer, out IDeviceBuffer sizeBuffer,
                                  out int dataStride, out int dataOffset, out int activationFunctionStride, out int activationFunctionOffset);

            int deltaOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, $"{nameof(NetworkDeltaBuffer.Data)}[0]");
            endOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, $"{nameof(NetworkDeltaBuffer.Data)}[1]");
            int deltaStride = endOffset - deltaOffset;

            // going to assume the data buffer has the same stride as the delta buffer
            int deltaBufferSize = deltaOffset + (outputCount * deltaStride + dataBuffer.Size - dataOffset) * passCount;
            var deltaBuffer = mContext.CreateDeviceBuffer(DeviceBufferUsage.Storage, deltaBufferSize);

            // vast training optimization
            // passing the ball into the gpus court
            deltaBuffer.Map(data =>
            {
                // pass count
                int passCountOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, nameof(NetworkDeltaBuffer.PassCount));
                BitConverter.GetBytes(passCount).CopyTo(data[passCountOffset..]);

                // learning rate
                int learningRateOffset = reflectionView.GetBufferOffset(ShaderResources.DeltaBufferName, nameof(NetworkDeltaBuffer.LearningRate));
                BitConverter.GetBytes(learningRate).CopyTo(data[learningRateOffset..]);
            });

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
                ActivationFunctionOffset = activationFunctionOffset,

                ActivationStride = activationStride,
                DeltaStride = deltaStride,
                DataStride = dataStride,
                ActivationFunctionStride = activationFunctionStride,

                PassCount = passCount,
                LayerSizes = layerSizes.ToArray()
            };
        }

        public static void ForwardPropagation(ICommandList commandList, DispatcherBufferData buffers, float[][] inputs)
        {
            AssertInitialized();

            using var forwardPropagationEvent = OptickMacros.Event();
            using var dispatchEvent = OptickMacros.GPUEvent("Forward propagation");

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
                using var copyEvent = OptickMacros.Event("Copy inputs to activation buffer");

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

            for (int i = 0; i < buffers.LayerSizes.Length - 1; i++)
            {
                if (i > 0)
                {
                    commandList.ExecutionBarrier();
                }

                pipeline.PushConstants(commandList, data =>
                {
                    pipeline.ReflectionView.MapStructure(data, ShaderResources.PushConstantBufferName, new NetworkPushConstantData
                    {
                        CurrentLayer = i + 1
                    });
                });

                int layerSize = buffers.LayerSizes[i + 1];
                mRenderer.DispatchCompute(commandList, buffers.PassCount, GetWorkGroupCount(layerSize), 1);
            }
        }

        public static void BackPropagation(ICommandList commandList, DispatcherBufferData buffers, float[][] expected)
        {
            AssertInitialized();

            using var backPropagationEvent = OptickMacros.Event();
            using var dispatchEvent = OptickMacros.GPUEvent("Back propagation");

            int outputCount = buffers.LayerSizes[^1];
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
                using var copyEvent = OptickMacros.Event("Copy expected outputs to delta buffer");

                for (int i = 0; i < buffers.PassCount; i++)
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

            int startingLayer = buffers.LayerSizes.Length - 1;
            for (int i = startingLayer; i > 0; i--)
            {
                if (i < startingLayer)
                {
                    commandList.ExecutionBarrier();
                }

                pipeline.PushConstants(commandList, data =>
                {
                    pipeline.ReflectionView.MapStructure(data, ShaderResources.PushConstantBufferName, new NetworkPushConstantData
                    {
                        CurrentLayer = i
                    });
                });

                int layerSize = buffers.LayerSizes[i];
                mRenderer.DispatchCompute(commandList, buffers.PassCount, GetWorkGroupCount(layerSize), 1);
            }
        }

        public static void DeltaComposition(ICommandList commandList, DispatcherBufferData buffers)
        {
            AssertInitialized();

            using var deltaCompositionEvent = OptickMacros.Event();
            using var gpuEvent = OptickMacros.GPUEvent("Delta composition");

            var pipeline = mLibrary.LoadPipeline<DeltaComposition>(new PipelineDescription
            {
                RenderTarget = null,
                Type = PipelineType.Compute,
                FrameCount = 1,
                Specification = null
            });

            pipeline.Bind(buffers.SizeBuffer, ShaderResources.SizeBufferName);
            pipeline.Bind(buffers.DataBuffer, ShaderResources.DataBufferName);
            pipeline.Bind(buffers.DeltaBuffer, ShaderResources.DeltaBufferName);

            commandList.PushStagingObject(pipeline);
            pipeline.Bind(commandList, 0);

            int maxLayerSize = -1;
            for (int i = 1; i < buffers.LayerSizes.Length; i++)
            {
                maxLayerSize = int.Max(maxLayerSize, buffers.LayerSizes[i]);
            }

            mRenderer.DispatchCompute(commandList, buffers.LayerSizes.Length, GetWorkGroupCount(maxLayerSize), 1);
        }

        public static float[][] GetConfidenceValues(DispatcherBufferData buffers)
        {
            AssertInitialized();
            using var getConfidenceEvent = OptickMacros.Event();

            int layerCount = buffers.LayerSizes.Length;
            int confidenceCount = buffers.LayerSizes[^1];

            int neuronTotal = buffers.LayerSizes.Aggregate((a, b) => a + b);
            int layerOffset = neuronTotal - confidenceCount;

            var results = new float[buffers.PassCount][];
            buffers.ActivationBuffer.Map(data =>
            {
                using var copyEvent = OptickMacros.Event("Copy from activation buffer");

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
    }
}
