using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    [CompiledShader]
    public sealed class ForwardPropagation
    {
        [Layout(Shared = true)]
        private static int s_DataOffset;
        [Layout(Shared = true)]
        private static int s_ActivationOffset;
        [Layout(Shared = true)]
        private static int s_PreviousActivationOffset;
        [Layout(Shared = true)]
        private static int s_PreSigmoidOffset;

        private static void Initialize(uint networkPass)
        {
            int currentLayer = ShaderResources.PushConstants.CurrentLayer;
            int currentPass = (int)networkPass;

            s_DataOffset = ShaderResources.GetLayerDataOffset(currentLayer);
            s_ActivationOffset = ShaderResources.GetLayerActivationOffset(currentPass, currentLayer, 0);
            s_PreSigmoidOffset = ShaderResources.GetLayerActivationOffset(currentPass, currentLayer, 1);
            s_PreviousActivationOffset = ShaderResources.GetLayerActivationOffset(currentPass, currentLayer - 1, 0);
        }

        public static float Sigmoid(float x) => 1f / (1f + BuiltinFunctions.Exp(-x));
        public static float ReLU(float x) => BuiltinFunctions.Max(0f, x);
        public static float LeakyReLU(float x) => BuiltinFunctions.Max(x * 0.1f, x);
        public static float NormalizedHyperbolicTangent(float x) => (BuiltinFunctions.Tanh(x) + 1f) / 2f;

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
                int biasOffset = ShaderResources.GetDataBlockBiasOffset(currentNeuron, previousLayerSize);

                // the bias preceeds all of the weights
                // initially setting z to the bias value
                // at the end of the loop, z will be the pre-sigmoidal input value
                float z = ShaderResources.DataBuffer.Data[biasOffset + s_DataOffset];
                for (int i = 0; i < previousLayerSize; i++)
                {
                    int weightOffset = ShaderResources.GetDataBlockWeightOffset(i, biasOffset);
                    float weight = ShaderResources.DataBuffer.Data[weightOffset + s_DataOffset];
                    float previousActivation = ShaderResources.ActivationBuffer.Data[s_PreviousActivationOffset + i];

                    z += weight * previousActivation;
                }

                float activation;
                ActivationFunction activationFunction = ShaderResources.DataBuffer.LayerActivationFunctions[currentLayer - 1];

                // not using a switch because the jumps are janky
                // and that is on my list! (nora)
                if (activationFunction == ActivationFunction.Sigmoid)
                {
                    activation = Sigmoid(z);
                }
                else if (activationFunction == ActivationFunction.ReLU)
                {
                    activation = ReLU(z);
                }
                else if (activationFunction == ActivationFunction.LeakyReLU)
                {
                    activation = LeakyReLU(z);
                }
                else if (activationFunction == ActivationFunction.NormalizedHyperbolicTangent)
                {
                    activation = NormalizedHyperbolicTangent(z);
                }
                else
                {
                    // not implemented
                    // cant exactly throw an exception
                    activation = 0f;
                }

                // s_PreSigmoidOffset describes the offset of the z-values of the current layer
                // s_ActivationOffset describes the offset of the activations of the current layer
                ShaderResources.PreSigmoidBuffer.Data[s_PreSigmoidOffset + currentNeuron] = z;
                ShaderResources.ActivationBuffer.Data[s_ActivationOffset + currentNeuron] = activation;
            }
        }
    }
}