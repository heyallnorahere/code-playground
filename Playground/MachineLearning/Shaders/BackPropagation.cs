using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    [CompiledShader]
    public sealed class BackPropagation
    {
        /// <summary>
        /// Offset of the current layer in the data buffer
        /// </summary>
        [Layout(Shared = true)]
        private static int s_DataOffset;

        /// <summary>
        /// Offset of the activations of the current layer into the activation buffer
        /// </summary>
        [Layout(Shared = true)]
        private static int s_ActivationOffset;

        /// <summary>
        /// Offset of the pre-activation values of the current layer in the corresponding buffer
        /// </summary>
        [Layout(Shared = true)]
        private static int s_PreSigmoidOffset;

        /// <summary>
        /// Offset of the deltas of the current layer within the current pass
        /// </summary>
        [Layout(Shared = true)]
        private static int s_DeltaOffset;

        /// <summary>
        /// Offset of the expected outputs for the current pass
        /// </summary>
        [Layout(Shared = true)]
        private static int s_DeltaExpectedOffset;

        public static float CostDerivative(float x, float y) => x - y; // partial derivative

        public static float SigmoidPrime(float x)
        {
            float sigmoid = ForwardPropagation.Sigmoid(x);
            return sigmoid * (1f - sigmoid);
        }

        public static float ReLUPrime(float x)
        {
            if (x > 0f)
            {
                return 1f;
            }
            else
            {
                return 0f;
            }
        }

        public static float LeakyReLUPrime(float x)
        {
            if (x > 0f)
            {
                return 1f;
            }
            else
            {
                return -0.1f;
            }
        }

        public static float NormalizedHyperbolicTangentPrime(float x)
        {
            float tanh = BuiltinFunctions.Tanh(x);
            return (1f - tanh * tanh) / 2f;
        }

        private static void Initialize(uint workGroupId)
        {
            int networkPass = (int)workGroupId;

            int lastLayer = ShaderResources.SizeBuffer.LayerCount - 1;
            int outputCount = ShaderResources.SizeBuffer.LayerSizes[lastLayer];

            s_DeltaOffset = outputCount * ShaderResources.DeltaBuffer.PassCount;
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
                    int blockSize = ForwardPropagation.GetDataBlockSize(currentLayerSize, previousLayerSize);

                    s_DeltaOffset += blockSize * offsetFactor;
                    s_DataOffset += blockSize * (offsetFactor - networkPass);
                }
            }
        }

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(Network.WorkGroupThreadCount, 1, 1)]
        public static void Entrypoint(ComputeInput input)
        {
            if (input.InvocationID.X == 0)
            {
                Initialize(input.WorkGroupID.X);
            }

            BuiltinFunctions.Barrier();

            int currentLayer = ShaderResources.PushConstants.CurrentLayer;
            int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer];
            
            int currentNeuron = (int)input.InvocationID.X + Network.WorkGroupThreadCount * (int)input.WorkGroupID.Y;
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
                ActivationFunction activationFunction = ShaderResources.DataBuffer.LayerActivationFunctions[currentLayer - 1];

                float activationPrime;
                if (activationFunction == ActivationFunction.Sigmoid)
                {
                    activationPrime = SigmoidPrime(z);
                }
                else if (activationFunction == ActivationFunction.ReLU)
                {
                    activationPrime = ReLUPrime(z);
                }
                else if (activationFunction == ActivationFunction.LeakyReLU)
                {
                    activationPrime = LeakyReLUPrime(z);
                }
                else if (activationFunction == ActivationFunction.NormalizedHyperbolicTangent)
                {
                    activationPrime = NormalizedHyperbolicTangentPrime(z);
                }
                else
                {
                    // see forwardpropagation
                    activationPrime = 0f;
                }

                float biasDelta = preSigmoidBias * activationPrime;
                ShaderResources.DeltaBuffer.Data[s_DeltaOffset + bufferRowOffset] = biasDelta;

                int previousLayerActivationOffset = s_ActivationOffset - previousLayerSize;
                for (int i = 0; i < previousLayerSize; i++)
                {
                    // setting weight (currentNeuron, i) to biasDeltas[currentNeuron] * previousActivation[i]
                    // dot product of delta vector and transposed activation vector
                    float previousActivation = ShaderResources.ActivationBuffer.Data[previousLayerActivationOffset + i];
                    ShaderResources.DeltaBuffer.Data[s_DeltaOffset + bufferRowOffset + i + 1] = previousActivation * biasDelta;
                }
            }
        }
    }
}