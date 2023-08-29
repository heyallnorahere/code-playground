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
        private static int s_PreSigmoidOffset;

        private static void Initialize(uint networkPass)
        {
            s_DataOffset = 0;
            s_ActivationOffset = 0;
            s_PreSigmoidOffset = 0;

            for (int i = 0; i < ShaderResources.SizeBuffer.LayerCount; i++)
            {
                int activationOffsetFactor = (int)networkPass;
                if (i < ShaderResources.PushConstants.CurrentLayer - 1)
                {
                    activationOffsetFactor++;
                }

                int layerSize = ShaderResources.SizeBuffer.LayerSizes[i];
                s_ActivationOffset += layerSize * activationOffsetFactor;

                if (i > 0)
                {
                    int preSigmoidOffsetFactor = (int)networkPass;

                    if (i < ShaderResources.PushConstants.CurrentLayer)
                    {
                        preSigmoidOffsetFactor++;

                        int previousLayerSize = ShaderResources.SizeBuffer.LayerSizes[i - 1];
                        s_DataOffset += GetDataBlockSize(layerSize, previousLayerSize);
                    }

                    s_PreSigmoidOffset += layerSize * preSigmoidOffsetFactor;
                }
            }
        }

        public static float Sigmoid(float x) => 1f / (1f + BuiltinFunctions.Exp(-x));

        // all of the weights relating to this neuron and the bias of the neuron
        public static int GetDataBlockRowLength(int previousLayerSize) => previousLayerSize + 1;
        public static int GetDataBlockSize(int currentLayerSize, int previousLayerSize) => GetDataBlockRowLength(previousLayerSize) * currentLayerSize;

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(Network.MaxNeuronsPerLayer, 1, 1)]
        public static void Entrypoint(ComputeInput input)
        {
            int currentNeuron = (int)input.InvocationID.X;
            if (currentNeuron == 0)
            {
                Initialize(input.WorkGroupID.X);
            }

            BuiltinFunctions.Barrier();

            int currentLayer = ShaderResources.PushConstants.CurrentLayer;
            int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer];

            if (currentNeuron < currentLayerSize)
            {
                int previousLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer - 1];
                int bufferRowOffset = currentNeuron * GetDataBlockRowLength(previousLayerSize) + s_DataOffset;

                // the bias preceeds all of the weights
                // initially setting z to the bias value
                // at the end of the loop, z will be the pre-sigmoidal input value
                float z = ShaderResources.DataBuffer.Data[bufferRowOffset];

                for (int i = 0; i < previousLayerSize; i++)
                {
                    float weight = ShaderResources.DataBuffer.Data[bufferRowOffset + i + 1];
                    float previousActivation = ShaderResources.ActivationBuffer.Data[s_ActivationOffset + i];

                    z += weight * previousActivation;
                }

                // s_PreSigmoidOffset describes the offset of the z-values of the current layer
                // s_ActivationOffset describes the offset of the activations of the previous layer
                ShaderResources.PreSigmoidBuffer.Data[s_PreSigmoidOffset + currentNeuron] = z;
                ShaderResources.ActivationBuffer.Data[s_ActivationOffset + previousLayerSize + currentNeuron] = Sigmoid(z);
            }
        }
    }
}