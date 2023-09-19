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
            s_DataOffset = ShaderResources.GetLayerDataOffset(currentLayer);
            s_DeltaOffset = ShaderResources.GetExpectedOutputOffset(ShaderResources.DeltaBuffer.PassCount);
            s_DeltaStride = ShaderResources.GetLayerDataOffset(ShaderResources.SizeBuffer.LayerCount);
            s_Scale = ShaderResources.DeltaBuffer.LearningRate / ShaderResources.DeltaBuffer.PassCount;
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

            if (currentNeuron < currentLayerSize)
            {
                // biasOffset is the offset of the current neuron's bias within a data matrix
                int biasOffset = s_DataOffset + ShaderResources.GetDataBlockBiasOffset(currentNeuron, previousLayerSize);
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
                        int weightOffset = ShaderResources.GetDataBlockWeightOffset(j, biasOffset);
                        int deltaWeightOffset = deltaMatrixOffset + weightOffset;

                        float deltaWeight = ShaderResources.DeltaBuffer.Data[deltaWeightOffset];
                        ShaderResources.DataBuffer.Data[weightOffset] -= deltaWeight * s_Scale;
                    }
                }
            }
        }
    }
}
