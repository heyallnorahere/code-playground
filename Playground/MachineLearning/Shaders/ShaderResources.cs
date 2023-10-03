using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    public struct ComputeInput
    {
        [ShaderVariable(ShaderVariableID.LocalInvocationID)]
        public Vector3<uint> InvocationID;

        [ShaderVariable(ShaderVariableID.WorkGroupID)]
        public Vector3<uint> WorkGroupID;

        [ShaderVariable(ShaderVariableID.WorkGroupCount)]
        public Vector3<uint> WorkGroupCount;
    }

    public struct SizeBufferData
    {
        public int LayerCount;

        [ArraySize(Network.MaxLayers)]
        public int[] LayerSizes;
    }

    public struct NetworkDataBuffer
    {
        [ArraySize(Network.MaxLayers - 1)]
        public ActivationFunction[] LayerActivationFunctions;

        public float[] Data;
    }

    public struct NetworkDeltaBuffer
    {
        public int PassCount;
        public float LearningRate;

        public float[] Data;
    }

    public struct NetworkArrayBuffer
    {
        public float[] Data;
    }

    public struct NetworkPushConstantData
    {
        public int CurrentLayer;
    }

    public static class ShaderResources
    {
        public const ShaderImageFormat ImageFormat = ShaderImageFormat.R16;

        public const string SizeBufferName = "u_NetworkSize";
        public const string DataBufferName = "u_DataBuffer";
        public const string ActivationBufferName = "u_ActivationBuffer";
        public const string PreSigmoidBufferName = "u_PreSigmoidBuffer";
        public const string DeltaBufferName = "u_DeltaBuffer";
        public const string OutputImageName = "u_OutputImage";
        public const string InputActivationImageName = "u_InputActivationImage";
        public const string PushConstantBufferName = "u_PushConstants";

        [Layout(Set = 0, Binding = 0, ResourceType = ShaderResourceType.Uniform)]
        [NamedShaderSymbol(SizeBufferName)]
        public static SizeBufferData SizeBuffer;

        [Layout(Set = 0, Binding = 1, ResourceType = ShaderResourceType.Storage)]
        [NamedShaderSymbol(DataBufferName)]
        public static NetworkDataBuffer DataBuffer;

        [Layout(Set = 0, Binding = 2, ResourceType = ShaderResourceType.Storage)]
        [NamedShaderSymbol(ActivationBufferName)]
        public static NetworkArrayBuffer ActivationBuffer;

        [Layout(Set = 0, Binding = 3, ResourceType = ShaderResourceType.Storage)]
        [NamedShaderSymbol(PreSigmoidBufferName)]
        public static NetworkArrayBuffer PreSigmoidBuffer;

        [Layout(Set = 0, Binding = 4, ResourceType = ShaderResourceType.Storage)]
        [NamedShaderSymbol(DeltaBufferName)]
        public static NetworkDeltaBuffer DeltaBuffer;

        [Layout(Set = 1, Binding = 0, Format = ShaderImageFormat.RG16)]
        [NamedShaderSymbol(OutputImageName)]
        public static Image2D<float>? OutputImage;

        [Layout(Set = 1, Binding = 1, Format = ShaderImageFormat.R16)]
        [NamedShaderSymbol(InputActivationImageName)]
        public static Image2D<float>? InputActivationImage;

        [Layout(PushConstant = true)]
        [NamedShaderSymbol(PushConstantBufferName)]
        public static NetworkPushConstantData PushConstants;

        public static int GetOutputCount()
        {
            int lastLayer = SizeBuffer.LayerCount - 1;
            return SizeBuffer.LayerSizes[lastLayer];
        }

        public static int GetExpectedOutputOffset(int networkPass)
        {
            int outputCount = GetOutputCount();
            return networkPass * outputCount;
        }

        public static int GetLayerActivationOffset(int networkPass, int layer, int startingLayer)
        {
            int offset = 0;
            for (int i = startingLayer; i < SizeBuffer.LayerCount; i++)
            {
                int layerSize = SizeBuffer.LayerSizes[i];
                int offsetFactor = networkPass;

                if (i < layer)
                {
                    offsetFactor++;
                }

                offset += layerSize * offsetFactor;
            }

            return offset;
        }

        // all of the weights relating to this neuron and the bias of the neuron
        public static int GetDataBlockRowLength(int previousLayerSize) => previousLayerSize + 1;
        public static int GetDataBlockSize(int currentLayerSize, int previousLayerSize) => GetDataBlockRowLength(previousLayerSize) * currentLayerSize;

        public static int GetLayerDataOffset(int layer)
        {
            int offset = 0;
            for (int i = 1; i < SizeBuffer.LayerCount; i++) // layer 0 is input
            {
                int currentLayerSize = SizeBuffer.LayerSizes[i];
                int previousLayerSize = SizeBuffer.LayerSizes[i - 1];

                if (i < layer)
                {
                    offset += GetDataBlockSize(currentLayerSize, previousLayerSize);
                }
            }

            return offset;
        }

        public static int GetDeltaPassOffset(int networkPass) => GetLayerDataOffset(SizeBuffer.LayerCount) * networkPass;

        public static int GetDataBlockBiasOffset(int currentNeuron, int previousLayerSize) => currentNeuron * GetDataBlockRowLength(previousLayerSize);
        public static int GetDataBlockWeightOffset(int previousNeuron, int currentNeuronBiasOffset) => currentNeuronBiasOffset + previousNeuron + 1; // magic number for bias
        public static int GetDataBlockWeightOffset(int currentNeuron, int previousNeuron, int previousLayerSize) => GetDataBlockWeightOffset(previousNeuron, GetDataBlockBiasOffset(currentNeuron, previousLayerSize));
    }
}