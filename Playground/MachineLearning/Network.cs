using CodePlayground.Graphics;
using MachineLearning.Shaders;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

        public readonly void SerializeToBuffer(Action<float> pushFloat)
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
        public Layer[] Data;
    }

    public sealed class Network
    {
        public const int MaxNeuronsPerLayer = 1024; // for now
        public const int MaxLayers = 16;

        public static Network Load(Stream stream, Encoding? encoding = null)
        {
            using var reader = new StreamReader(stream, encoding: encoding ?? Encoding.UTF8, leaveOpen: true);
            using var jsonReader = new JsonTextReader(reader);

            var data = App.Serializer.Deserialize<NetworkData>(jsonReader);
            return new Network(data);
        }

        public static void Save(Network network, Stream stream, Encoding? encoding = null)
        {
            using var writer = new StreamWriter(stream, encoding: encoding ?? Encoding.UTF8, leaveOpen: true);
            using var jsonWriter = new JsonTextWriter(writer);

            var data = network.GetData();
            App.Serializer.Serialize(jsonWriter, data);
        }

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

        public Network(NetworkData data)
        {
            int layerCount = data.LayerSizes.Length;
            if (data.Data.Length != layerCount - 1)
            {
                throw new ArgumentException("Layer count mismatch!");
            }

            mLayerSizes = new int[layerCount];
            mLayers = new Layer[layerCount - 1];

            Array.Copy(data.LayerSizes, mLayerSizes, layerCount);
            for (int i = 0; i < layerCount - 1; i++)
            {
                int currentSize = mLayerSizes[i + 1];
                int previousSize = mLayerSizes[i];

                var layer = new Layer(currentSize, previousSize);
                var existingLayer = data.Data[i];

                if (existingLayer.Biases.Length != existingLayer.Weights.GetLength(0))
                {
                    throw new ArgumentException("Bias/weight size mismatch!");
                }

                if (existingLayer.Weights.GetLength(0) != currentSize ||
                    existingLayer.Weights.GetLength(1) != previousSize)
                {
                    throw new ArgumentException("Network size mismatch!");
                }

                for (int y = 0; y < currentSize; y++)
                {
                    layer.Biases[y] = existingLayer.Biases[y];
                    for (int x = 0; x < previousSize; x++)
                    {
                        layer.Weights[y, x] = existingLayer.Weights[y, x];
                    }
                }

                mLayers[i] = layer;
            }
        }

        public void CreateBuffers(IGraphicsContext context, IReflectionView reflectionView, out IDeviceBuffer dataBuffer, out IDeviceBuffer sizeBuffer)
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
                    mLayers[i].SerializeToBuffer(values.Add);

                    foreach (var value in values)
                    {
                        int offset = n * stride + startOffset;
                        BitConverter.GetBytes(value).CopyTo(data[offset..]);
                    }
                }
            });
        }

        public IReadOnlyList<int> LayerSizes => mLayerSizes;

        public NetworkData GetData()
        {
            int layerCount = mLayerSizes.Length;
            var result = new NetworkData
            {
                LayerSizes = new int[layerCount],
                Data = new Layer[layerCount - 1]
            };

            Array.Copy(mLayerSizes, result.LayerSizes, layerCount);
            for (int i = 0; i < layerCount - 1; i++)
            {
                int currentSize = mLayerSizes[i + 1];
                int previousSize = mLayerSizes[i];

                var layer = new Layer(currentSize, previousSize);
                var existingLayer = mLayers[i];

                for (int y = 0; y < currentSize; y++)
                {
                    layer.Biases[y] = existingLayer.Biases[y];
                    for (int x = 0; x <  previousSize; x++)
                    {
                        layer.Weights[y, x] = existingLayer.Weights[y, x];
                    }
                }

                result.Data[i] = layer;
            }

            return result;
        }

        private readonly int[] mLayerSizes;
        private readonly Layer[] mLayers;
    }
}