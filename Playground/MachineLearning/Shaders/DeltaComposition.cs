using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    [CompiledShader]
    public sealed class DeltaComposition
    {
        [Layout(Shared = true)]
        private static int s_DataOffset;
        [Layout(Shared = true)]
        private static int s_DeltaOffset;
        [Layout(Shared = true)]
        private static int s_DeltaStride;
        [Layout(Shared = true)]
        private static float s_Scale;

        private static void Initialize(int currentLayer)
        {
            int lastLayer = ShaderResources.SizeBuffer.LayerCount - 1;
            int outputCount = ShaderResources.SizeBuffer.LayerSizes[lastLayer];

            s_DataOffset = 0;
            s_DeltaOffset = outputCount * ShaderResources.DeltaBuffer.PassCount; // expected outputs for backprop
            s_DeltaStride = 0;
            s_Scale = ShaderResources.DeltaBuffer.LearningRate / ShaderResources.DeltaBuffer.PassCount;

            int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[0];
            for (int i = 1; i < ShaderResources.SizeBuffer.LayerCount; i++)
            {
                int previousLayerSize = currentLayerSize;
                currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[i];

                // every neuron in the current layer has a weight with every neuron in the previous layer, plus a bias, hence the +1
                int matrixSize = currentLayerSize * (previousLayerSize + 1);

                // s_DeltaStride is the count of deltas produced by 1 pass of backprop
                s_DeltaStride += matrixSize;
                if (i < currentLayer)
                {
                    s_DataOffset += matrixSize;
                }
            }
        }

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(Network.WorkGroupThreadCount, 1, 1)]
        public static void Entrypoint(ComputeInput input)
        {
            int currentLayer = (int)input.WorkGroupID.X + 1;
            int currentNeuron = (int)input.InvocationID.X + Network.WorkGroupThreadCount * (int)input.WorkGroupID.Y;

            if (input.InvocationID.X == 0)
            {
                Initialize(currentLayer);
            }

            BuiltinFunctions.Barrier();

            int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer];
            int previousLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer - 1];

            if (currentNeuron >= currentLayerSize)
            {
                return;
            }

            // neuronOffset is the offset of the current neuron's weights and biases within a "data" matrix
            // biasOffset is the offset of the current neuron's bias within a data matrix
            int neuronOffset = currentNeuron * (previousLayerSize + 1); // neuron weights plus bias
            int biasOffset = s_DataOffset + neuronOffset;

            for (int i = 0; i < ShaderResources.DeltaBuffer.PassCount; i++)
            {
                // deltaMatrixOffset is the offset of the deltas of the current "pass" (network invocation)
                // deltaBiasOffset is the offset of the current neuron's bias delta in the current pass
                int deltaMatrixOffset = s_DeltaOffset + s_DeltaStride * i;
                int deltaBiasOffset = deltaMatrixOffset + biasOffset;

                float deltaBias = ShaderResources.DeltaBuffer.Data[deltaBiasOffset];
                ShaderResources.DataBuffer.Data[biasOffset] -= deltaBias * s_Scale;

                for (int j = 0; j < previousLayerSize; j++)
                {
                    // weightOffset is the offset of the current neuron's weight in relation to a neuron from the previous layer within a data matrix
                    // deltaWeightOffset is the offset of said weight within a current pass
                    int weightOffset = biasOffset + j + 1;
                    int deltaWeightOffset = deltaMatrixOffset + weightOffset;

                    float deltaWeight = ShaderResources.DeltaBuffer.Data[deltaWeightOffset];
                    ShaderResources.DataBuffer.Data[weightOffset] -= deltaWeight * s_Scale;
                }
            }
        }
    }
}
