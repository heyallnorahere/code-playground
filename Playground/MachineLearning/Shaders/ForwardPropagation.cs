using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    [CompiledShader]
    public sealed class ForwardPropagation
    {
        public struct Input
        {
            [ShaderVariable(ShaderVariableID.LocalInvocationID)]
            public Vector3<uint> InvocationID;

            [ShaderVariable(ShaderVariableID.WorkGroupID)]
            public Vector3<uint> WorkGroupID;
        }

        [Layout(Shared = true)]
        private static int s_ActivatedNeurons;
        [Layout(Shared = true)]
        private static int s_CurrentLayer;
        [Layout(Shared = true)]
        private static int s_MaxLayerSize;
        [Layout(Shared = true)]
        private static int s_DataOffset;
        [Layout(Shared = true)]
        private static int s_ActivationOffset;

        public static float Sigmoid(float x) => 1 / (1 - BuiltinFunctions.Exp(-x));

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(Network.MaxNeuronsPerLayer, 1, 1)]
        public static void Entrypoint(Input input)
        {
            int currentNeuron = (int)input.InvocationID.X;
            if (currentNeuron == 0)
            {
                s_ActivatedNeurons = 0;
                s_CurrentLayer = 1;
                s_DataOffset = 0;

                s_ActivationOffset = 0;
                s_MaxLayerSize = 0;
                for (int i = 0; i < ShaderResources.SizeBuffer.LayerCount; i++)
                {
                    int layerSize = ShaderResources.SizeBuffer.LayerSizes[i];
                    s_ActivationOffset += layerSize * (int)input.WorkGroupID.X;

                    if (i > 0)
                    {
                        s_MaxLayerSize = BuiltinFunctions.Max(s_MaxLayerSize, layerSize);
                    }
                }
            }

            BuiltinFunctions.Barrier();
            if (currentNeuron >= s_MaxLayerSize)
            {
                return;
            }

            int currentLayer = s_CurrentLayer;
            while (currentLayer < ShaderResources.SizeBuffer.LayerCount)
            {
                int currentLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer];
                if (currentNeuron < currentLayerSize)
                {
                    int previousLayerSize = ShaderResources.SizeBuffer.LayerSizes[currentLayer - 1];

                    // all of the weights relating to this neuron, plus the bias
                    int rowLength = previousLayerSize + 1;
                    int rowOffset = currentNeuron * rowLength + s_DataOffset;

                    // the bias preceeds all of the weights
                    float z = ShaderResources.DataBuffer.Data[rowOffset];

                    for (int i = 0; i < previousLayerSize; i++)
                    {
                        float weight = ShaderResources.DataBuffer.Data[rowOffset + i + 1];
                        float previousActivation = ShaderResources.ActivationBuffer.Data[s_ActivationOffset + i];

                        z += weight * previousActivation;
                    }

                    // s_ActivationOffset describes the offset of the activations of the previous layer
                    ShaderResources.ActivationBuffer.Data[s_ActivationOffset + previousLayerSize + currentNeuron] = Sigmoid(z);

                    int previousValue = Atomic.Add(s_ActivatedNeurons, 1);
                    if (previousValue == currentLayerSize - 1)
                    {
                        s_ActivatedNeurons = 0;
                        Atomic.Add(s_DataOffset, rowLength * currentLayerSize);
                        Atomic.Add(s_ActivationOffset, previousLayerSize);
                        Atomic.Add(s_CurrentLayer, 1);
                    }
                }

                while (currentLayer == s_CurrentLayer)
                {
                    // wait
                }

                currentLayer = s_CurrentLayer;
            }
        }
    }
}