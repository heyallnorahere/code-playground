using CodePlayground;
using CodePlayground.Graphics;
using MachineLearning.Shaders;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;

namespace MachineLearning
{
    public struct DispatcherBufferData : IDisposable
    {
        public void Dispose()
        {
            ActivationBuffer.Dispose();
            PreSigmoidBuffer.Dispose();
            SizeBuffer.Dispose();
            DataBuffer.Dispose();
            DeltaBuffer.Dispose();

            InputActivationImage.Dispose();
            OutputImage.Dispose();
        }

        public IDeviceBuffer ActivationBuffer, PreSigmoidBuffer, SizeBuffer, DataBuffer, DeltaBuffer;
        public int ActivationOffset, DeltaOffset, DataOffset, ActivationFunctionOffset;
        public int ActivationStride, DeltaStride, DataStride, ActivationFunctionStride;

        public IDeviceImage InputActivationImage, OutputImage;

        public int PassCount;
        public int[] LayerSizes;
    }

    /// <summary>
    /// Requires a queue with compute capabilities
    /// </summary>
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

            using var createBuffersEvent = Profiler.Event();
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

                InputActivationImage = mContext.CreateDeviceImage(new DeviceImageInfo
                {
                    Size = new Size(inputCount, passCount),
                    Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.Storage,
                    Format = DeviceImageFormat.R16_UNORM,
                    MipLevels = 1
                }),

                OutputImage = mContext.CreateDeviceImage(new DeviceImageInfo
                {
                    Size = new Size(outputCount, passCount),
                    Usage = DeviceImageUsageFlags.CopySource | DeviceImageUsageFlags.Storage,
                    Format = DeviceImageFormat.RG16_UNORM,
                    MipLevels = 1
                }),

                PassCount = passCount,
                LayerSizes = layerSizes.ToArray()
            };
        }

        public static void TransitionImages(ICommandList commandList, DispatcherBufferData bufferData)
        {
            var storageLayout = bufferData.InputActivationImage.GetLayout(DeviceImageLayoutName.ComputeStorage);

            bufferData.InputActivationImage.TransitionLayout(commandList, bufferData.InputActivationImage.Layout, storageLayout);
            bufferData.InputActivationImage.Layout = storageLayout;

            bufferData.OutputImage.TransitionLayout(commandList, bufferData.OutputImage.Layout, storageLayout);
            bufferData.OutputImage.Layout = storageLayout;
        }

        public static void ForwardPropagation(ICommandList commandList, DispatcherBufferData buffers, float[][]? inputs)
        {
            AssertInitialized();

            using var forwardPropagationEvent = Profiler.Event();
            using var dispatchEvent = Profiler.GPUEvent(commandList, "Forward propagation");

            int inputCount = buffers.LayerSizes[0];
            int neuronTotal = buffers.LayerSizes.Aggregate((a, b) => a + b);

            if (buffers.PassCount > mMaxConcurrentPasses)
            {
                throw new ArgumentException($"Cannot execute more than {mMaxConcurrentPasses} passes at a time!");
            }

            if (inputs is not null)
            {
                if (inputs.Length != buffers.PassCount)
                {
                    throw new ArgumentException("Inconsistent pass count!");
                }

                buffers.ActivationBuffer.Map(data =>
                {
                    using var copyEvent = Profiler.Event("Copy inputs to activation buffer");

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
            }

            var pipeline = mLibrary.LoadPipeline<ForwardPropagation>(new PipelineDescription
            {
                RenderTarget = null,
                Type = PipelineType.Compute,
                FrameCount = 1,
                Specification = null
            });

            pipeline.Bind(buffers.SizeBuffer, ShaderResources.SizeBufferName);
            pipeline.Bind(buffers.DataBuffer, ShaderResources.DataBufferName);
            pipeline.Bind(buffers.ActivationBuffer, ShaderResources.ActivationBufferName);
            pipeline.Bind(buffers.PreSigmoidBuffer, ShaderResources.PreSigmoidBufferName);
            pipeline.Bind(buffers.InputActivationImage, ShaderResources.InputActivationImageName);

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

            using var backPropagationEvent = Profiler.Event();
            using var dispatchEvent = Profiler.GPUEvent(commandList, "Back propagation");

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
                using var copyEvent = Profiler.Event("Copy expected outputs to delta buffer");

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
            pipeline.Bind(buffers.OutputImage, ShaderResources.OutputImageName);

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

            using var deltaCompositionEvent = Profiler.Event();
            using var gpuEvent = Profiler.GPUEvent(commandList, "Delta composition");

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

            mRenderer.DispatchCompute(commandList, buffers.LayerSizes.Length - 1, GetWorkGroupCount(maxLayerSize), 1);
        }

        public static float[][] GetConfidenceValues(DispatcherBufferData buffers)
        {
            AssertInitialized();
            using var getConfidenceEvent = Profiler.Event();

            int layerCount = buffers.LayerSizes.Length;
            int confidenceCount = buffers.LayerSizes[^1];

            int neuronTotal = buffers.LayerSizes.Aggregate((a, b) => a + b);
            int layerOffset = neuronTotal - confidenceCount;

            var results = new float[buffers.PassCount][];
            buffers.ActivationBuffer.Map(data =>
            {
                using var copyEvent = Profiler.Event("Copy from activation buffer");

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

        public static IReadOnlyList<float> DumpBuffer(ReadOnlySpan<byte> buffer, int stride, int startOffset, int endOffset = -1)
        {
            int currentOffset = startOffset;
            int end = endOffset < 0 || endOffset > buffer.Length ? buffer.Length : endOffset;

            var data = new List<float>();
            while (currentOffset < end - stride)
            {
                var slice = buffer[currentOffset..];
                float value = BitConverter.ToSingle(slice);

                data.Add(value);
                currentOffset += stride;
            }

            return data;
        }

        public static IReadOnlyList<float> DumpBuffer(IDeviceBuffer buffer, int stride, int startOffset, int endOffset = -1)
        {
            IReadOnlyList<float>? result = null;
            buffer.Map(data => result = DumpBuffer(data, stride, startOffset, endOffset));

            return result ?? throw new InvalidOperationException("Failed to map buffer!");
        }
    }
}
