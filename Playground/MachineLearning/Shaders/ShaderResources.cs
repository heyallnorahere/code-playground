using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    public struct ComputeInput
    {
        [ShaderVariable(ShaderVariableID.LocalInvocationID)]
        public Vector3<uint> InvocationID;

        [ShaderVariable(ShaderVariableID.WorkGroupID)]
        public Vector3<uint> WorkGroupID;
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
        public const string SizeBufferName = "u_NetworkSize";
        public const string DataBufferName = "u_DataBuffer";
        public const string ActivationBufferName = "u_ActivationBuffer";
        public const string PreSigmoidBufferName = "u_PreSigmoidBuffer";
        public const string DeltaBufferName = "u_DeltaBuffer";
        public const string PushConstantBufferName = "u_PushConstants";

        [Layout(Set = 0, Binding = 0, ResourceType = ShaderResourceType.Uniform)]
        [ShaderFieldName(SizeBufferName, UseClassName = false)]
        public static SizeBufferData SizeBuffer;

        [Layout(Set = 0, Binding = 1, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(DataBufferName, UseClassName = false)]
        public static NetworkDataBuffer DataBuffer;

        [Layout(Set = 0, Binding = 2, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(ActivationBufferName, UseClassName = false)]
        public static NetworkArrayBuffer ActivationBuffer;

        [Layout(Set = 0, Binding = 3, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(PreSigmoidBufferName, UseClassName = false)]
        public static NetworkArrayBuffer PreSigmoidBuffer;

        [Layout(Set = 0, Binding = 4, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(DeltaBufferName, UseClassName = false)]
        public static NetworkDeltaBuffer DeltaBuffer;

        [Layout(PushConstant = true)]
        [ShaderFieldName(PushConstantBufferName, UseClassName = false)]
        public static NetworkPushConstantData PushConstants;
    }
}