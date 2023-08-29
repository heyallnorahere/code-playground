using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    [CompiledShader]
    public sealed class BackPropagation
    {
        [Layout(Shared = true)]
        private static int s_DataOffset;
        [Layout(Shared = true)]
        private static int s_ActivationOffset;
        [Layout(Shared = true)]
        private static int s_PreSigmoidOffset;
        [Layout(Shared = true)]
        private static int s_DeltaOffset;
        [Layout(Shared = true)]
        private static int s_DeltaExpectedOffset;

        public static float CostDerivative(float x, float y) => x - y; // partial derivative

        public static float SigmoidPrime(float x)
        {
            float sigmoid = ForwardPropagation.Sigmoid(x);
            return sigmoid * (1f - sigmoid);
        }

        private static void Initialize(uint workGroupId, uint workGroupCount)
        {
            int networkPass = (int)workGroupId;
            int passCount = (int)workGroupCount;

            int lastLayer = ShaderResources.SizeBuffer.LayerCount - 1;
            int outputCount = ShaderResources.SizeBuffer.LayerSizes[lastLayer];

            s_DeltaOffset = outputCount * passCount;
            s_DeltaExpectedOffset = networkPass * outputCount;

            s_DataOffset = 0;
            s_ActivationOffset = 0;
            s_PreSigmoidOffset = 0;

            for (int i = 0; i < ShaderResources.SizeBuffer.LayerCount; i++)
            {
                int offsetFactor = networkPass;
                if (i < ShaderResources.PushConstants.CurrentLayer)
                {
                    offsetFactor++;
                }

                int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[i];
                int activationIncrement = currentLayerSize * offsetFactor;

                s_ActivationOffset += activationIncrement;
                if (i > 0)
                {
                    s_PreSigmoidOffset += activationIncrement;

                    int previousLayerSize = ShaderResources.SizeBuffer.LayerSizes[i - 1];
                    int blockSize = ForwardPropagation.GetDataBlockSize(currentLayerSize, previousLayerSize) * offsetFactor;

                    s_DataOffset += blockSize;
                    s_DeltaOffset += blockSize;
                }
            }
        }

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(Network.MaxNeuronsPerLayer, 1, 1)]
        public static void Entrypoint(ComputeInput input)
        {
            int currentNeuron = (int)input.InvocationID.X;
            if (currentNeuron == 0)
            {
                Initialize(input.WorkGroupID.X, input.WorkGroupCount.X);
            }

            BuiltinFunctions.Barrier();

            int currentLayer = ShaderResources.PushConstants.CurrentLayer;
            int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer];
            if (currentNeuron < currentLayerSize)
            {
                int previousLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer - 1];
                int bufferRowOffset = currentNeuron * ForwardPropagation.GetDataBlockRowLength(previousLayerSize);
                int dataBlockSize = ForwardPropagation.GetDataBlockSize(currentLayerSize, previousLayerSize);

                // algorithm based off of
                // https://github.com/yodasoda1219/cpu-neural-network/blob/8ae7a36316b7bffb271b551f1f0da767c6b0a74e/NeuralNetwork/Network.cs#L156

                float preSigmoidBias;
                if (currentLayer == ShaderResources.SizeBuffer.LayerCount - 1)
                {
                    // s_ActivationOffset is of the CURRENT layer, in contrast to ForwardPropagation
                    float activation = ShaderResources.ActivationBuffer.Data[s_ActivationOffset + currentNeuron];
                    float expected = ShaderResources.DeltaBuffer.Data[s_DeltaExpectedOffset + currentNeuron];
                    preSigmoidBias = CostDerivative(activation, expected);
                }
                else
                {
                    preSigmoidBias = 0f;

                    int nextLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer + 1];
                    int nextLayerDataOffset = s_DataOffset + dataBlockSize;
                    int nextLayerDeltaOffset = s_DeltaOffset + dataBlockSize;
                    int nextLayerDataBlockRowLength = ForwardPropagation.GetDataBlockRowLength(currentLayerSize);

                    // taking the dot product of the transposed weights matrix of the next layer, and the deltas that we previously computed
                    // thus taking the dot product of the currentNeuron-th column, and the deltas
                    for (int i = 0; i < nextLayerSize; i++)
                    {
                        int weightMatrixRowOffset = nextLayerDataBlockRowLength * i;
                        int weightMatrixOffset = weightMatrixRowOffset + currentNeuron + 1;

                        float weight = ShaderResources.DataBuffer.Data[nextLayerDataOffset + weightMatrixOffset];
                        float previousBiasDelta = ShaderResources.DeltaBuffer.Data[nextLayerDeltaOffset + weightMatrixRowOffset];
                        preSigmoidBias += weight * previousBiasDelta;
                    }
                }

                float z = ShaderResources.PreSigmoidBuffer.Data[s_PreSigmoidOffset + currentNeuron];
                float biasDelta = preSigmoidBias * SigmoidPrime(z);
                ShaderResources.DeltaBuffer.Data[s_DeltaOffset + bufferRowOffset] = biasDelta;

                int previousLayerActivationOffset = s_ActivationOffset - previousLayerSize;
                for (int i = 0; i < previousLayerSize; i++)
                {
                    float previousActivation = ShaderResources.ActivationBuffer.Data[previousLayerActivationOffset + i];
                    ShaderResources.DeltaBuffer.Data[s_DeltaOffset + bufferRowOffset + i + 1] = previousActivation * biasDelta;
                }
            }
        }
    }
}