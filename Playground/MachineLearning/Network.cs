using CodePlayground.Graphics;
using MachineLearning.Shaders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

// based off of https://github.com/yodasoda1219/cpu-neural-network/blob/main/NeuralNetwork/Network.cs
namespace MachineLearning
{
    public struct Layer
    {
        public Layer(int currentSize, int previousSize)
        {
            // we want to transform a vector of activations from the previous layer into a vector of partial Z values of the current layer
            // thus, current by previous size matrix

            Weights = new float[currentSize, previousSize];
            Biases = new float[currentSize];
        }

        public float[,] Weights;
        public float[] Biases;

        public readonly void Serialize(Action<float> pushFloat)
        {
            int currentSize = Weights.GetLength(0);
            int previousSize = Weights.GetLength(1);

            if (Biases.Length != currentSize)
            {
                throw new ArgumentException("Size mismatch!");
            }

            for (int i = 0; i < currentSize; i++)
            {
                pushFloat(Biases[i]);
                for (int j = 0; j < previousSize; j++)
                {
                    pushFloat(Weights[i, j]);
                }
            }
        }
    }

    public struct NetworkData
    {
        public int[] LayerSizes;
        public Layer[] LayerData;
    }

    public sealed class Network
    {
        public const int MaxNeuronsPerLayer = 1024; // for now
        public const int MaxLayers = 16;

        public Network(IReadOnlyList<int> layerSizes)
        {
            mLayerSizes = layerSizes.ToArray();
            if (mLayerSizes.Length > MaxLayers)
            {
                throw new ArgumentException($"Must have at most {MaxLayers} layers!");
            }
        
            mLayers = new Layer[mLayerSizes.Length - 1];
            for (int i = 0; i < mLayers.Length; i++)
            {
                int previousSize = mLayerSizes[i];
                int currentSize = mLayerSizes[i + 1];

                var layer = new Layer(currentSize, previousSize);
                for (int y = 0; y < currentSize; y++)
                {
                    layer.Biases[y] = App.RNG.NextSingle() * 2f - 1f;
                    for (int x = 0; x < previousSize; x++)
                    {
                        layer.Weights[y, x] = App.RNG.NextSingle() * 2f - 1f;
                    }
                }
            }
        }

        public void Serialize(IGraphicsContext context, IReflectionView reflectionView, out IDeviceBuffer dataBuffer, out IDeviceBuffer sizeBuffer)
        {
            int bufferSize = reflectionView.GetBufferSize(ShaderResources.SizeBufferName);
            sizeBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Uniform, bufferSize);

            sizeBuffer.Map(data =>
            {
                int layerCount = mLayerSizes.Length;
                int offset = reflectionView.GetBufferOffset(ShaderResources.SizeBufferName, nameof(SizeBufferData.LayerCount));
                BitConverter.GetBytes(layerCount).CopyTo(data[offset..]);

                for (int i = 0; i < layerCount; i++)
                {
                    offset = reflectionView.GetBufferOffset(ShaderResources.SizeBufferName, $"{nameof(SizeBufferData.LayerSizes)}[{i}]");
                    BitConverter.GetBytes(mLayerSizes[i]).CopyTo(data[offset..]);
                }
            });

            int startOffset = reflectionView.GetBufferOffset(ShaderResources.DataBufferName, $"{nameof(NetworkDataBuffer.Data)}[0]");
            int endOffset = reflectionView.GetBufferOffset(ShaderResources.DataBufferName, $"{nameof(NetworkDataBuffer.Data)}[1]");
            int stride = endOffset - startOffset;

            int elementCount = 0;
            for (int i = 0; i < mLayers.Length; i++)
            {
                int previousSize = mLayerSizes[i];
                int currentSize = mLayerSizes[i + 1];

                elementCount += currentSize * (previousSize + 1);
            }

            dataBuffer = context.CreateDeviceBuffer(DeviceBufferUsage.Storage, elementCount * stride + startOffset);
            dataBuffer.Map(data =>
            {
                int n = 0;
                for (int i = 0; i < mLayers.Length; i++)
                {
                    var values = new List<float>();
                    mLayers[i].Serialize(values.Add);

                    foreach (var value in values)
                    {
                        int offset = reflectionView.GetBufferOffset(ShaderResources.DataBufferName, $"{nameof(NetworkDataBuffer.Data)}[{n++}]");
                        BitConverter.GetBytes(value).CopyTo(data[offset..]);
                    }
                }
            });
        }

        private readonly int[] mLayerSizes;
        private readonly Layer[] mLayers;
    }
}