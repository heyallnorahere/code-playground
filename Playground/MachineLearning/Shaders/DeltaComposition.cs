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
            s_DeltaOffset = outputCount * ShaderResources.DeltaBuffer.PassCount;
            s_DeltaStride = 0;
            s_Scale = ShaderResources.DeltaBuffer.LearningRate / ShaderResources.DeltaBuffer.PassCount;

            for (int i = 1; i < ShaderResources.SizeBuffer.LayerCount; i++)
            {
                int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[i];
                int previousLayerSize = ShaderResources.SizeBuffer.LayerSizes[i - 1];
                int matrixSize = currentLayerSize * (previousLayerSize + 1);

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

            int matrixOffset = currentNeuron * (previousLayerSize + 1); // neuron weights plus bias
            int biasOffset = s_DataOffset + matrixOffset;

            for (int i = 0; i < ShaderResources.DeltaBuffer.PassCount; i++)
            {
                int deltaBiasOffset = s_DeltaOffset + s_DeltaStride * i + biasOffset;

                float deltaBias = ShaderResources.DeltaBuffer.Data[deltaBiasOffset];
                ShaderResources.DataBuffer.Data[biasOffset] -= deltaBias * s_Scale;

                for (int j = 0; j < previousLayerSize; j++)
                {
                    int weightOffset = biasOffset + j + 1;
                    int deltaWeightOffset = s_DeltaOffset + s_DeltaStride * i + weightOffset;

                    float deltaWeight = ShaderResources.DeltaBuffer.Data[deltaWeightOffset];
                    ShaderResources.DataBuffer.Data[weightOffset] -= deltaWeight * s_Scale;
                }
            }
        }
    }
}
