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
            int currentLayer = ShaderResources.PushConstants.CurrentLayer;

            // expected outputs are placed before deltas
            // expected outputs are input to backprop
            // deltas are output
            s_DeltaOffset = ShaderResources.GetExpectedOutputOffset(ShaderResources.DeltaBuffer.PassCount); // this is CONSTANT for this group of backprop dispatches
            s_DeltaExpectedOffset = ShaderResources.GetExpectedOutputOffset(networkPass);

            s_DataOffset = ShaderResources.GetLayerDataOffset(currentLayer);
            s_ActivationOffset = ShaderResources.GetLayerActivationOffset(networkPass, currentLayer, 0);
            s_PreSigmoidOffset = ShaderResources.GetLayerActivationOffset(networkPass, currentLayer, 1);

            s_DeltaOffset += s_DataOffset + ShaderResources.GetDeltaPassOffset(networkPass);
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

                // algorithm based off of
                // https://github.com/yodasoda1219/cpu-neural-network/blob/8ae7a36316b7bffb271b551f1f0da767c6b0a74e/NeuralNetwork/Network.cs#L156

                float preSigmoidBias;
                if (currentLayer == ShaderResources.SizeBuffer.LayerCount - 1)
                {
                    // s_ActivationOffset is of the CURRENT layer
                    float activation = ShaderResources.ActivationBuffer.Data[s_ActivationOffset + currentNeuron];
                    float expected = ShaderResources.DeltaBuffer.Data[s_DeltaExpectedOffset + currentNeuron];
                    preSigmoidBias = CostDerivative(activation, expected);
                }
                else
                {
                    preSigmoidBias = 0f;

                    int nextLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer + 1];
                    int dataBlockSize = ShaderResources.GetDataBlockSize(currentLayerSize, previousLayerSize);
                    int nextLayerDataOffset = s_DataOffset + dataBlockSize;
                    int nextLayerDeltaOffset = s_DeltaOffset + dataBlockSize;

                    // taking the dot product of the transposed weights matrix of the next layer, and the deltas that we previously computed
                    // thus taking the dot product of the currentNeuron-th column, and the deltas
                    for (int i = 0; i < nextLayerSize; i++)
                    {
                        int previousBiasOffset = ShaderResources.GetDataBlockBiasOffset(i, currentLayerSize);
                        int previousWeightOffset = ShaderResources.GetDataBlockWeightOffset(currentNeuron, previousBiasOffset);

                        float weight = ShaderResources.DataBuffer.Data[nextLayerDataOffset + previousWeightOffset];
                        float previousBiasDelta = ShaderResources.DeltaBuffer.Data[nextLayerDeltaOffset + previousBiasOffset];
                        preSigmoidBias += weight * previousBiasDelta;
                    }
                }

                float z = ShaderResources.PreSigmoidBuffer.Data[s_PreSigmoidOffset + currentNeuron];
                ActivationFunction activationFunction = ShaderResources.DataBuffer.LayerActivationFunctions[currentLayer - 1]; // no activation function for input layer

                // GLSLTranspiler does not handle jumps well
                // todo(me): fix
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

                int biasOffset = ShaderResources.GetDataBlockBiasOffset(currentNeuron, previousLayerSize);
                float biasDelta = preSigmoidBias * activationPrime;
                ShaderResources.DeltaBuffer.Data[s_DeltaOffset + biasOffset] = biasDelta;

                // see earlier, s_ActivationOffset is of the current layer
                int previousLayerActivationOffset = s_ActivationOffset - previousLayerSize;
                for (int i = 0; i < previousLayerSize; i++)
                {
                    int weightOffset = ShaderResources.GetDataBlockWeightOffset(i, biasOffset);

                    // setting weight (currentNeuron, i) to biasDeltas[currentNeuron] * previousActivation[i]
                    // dot product of delta vector and transposed activation vector
                    float previousActivation = ShaderResources.ActivationBuffer.Data[previousLayerActivationOffset + i];
                    ShaderResources.DeltaBuffer.Data[s_DeltaOffset + weightOffset] = previousActivation * biasDelta; // magic number - bias before weights
                }
            }
        }
    }
}