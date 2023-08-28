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
        public float[] Data;
    }

    public static class ShaderResources
    {
        public const string SizeBufferName = "u_NetworkSize";
        public const string DataBufferName = "u_DataBuffer";
        public const string ActivationBufferName = "u_ActivationBuffer";
        public const string PreSigmoidBufferName = "u_PreSigmoidBuffer";
        public const string DeltaBufferName = "u_DeltaBuffer";

        [Layout(Set = 0, Binding = 0, ResourceType = ShaderResourceType.Uniform)]
        [ShaderFieldName(SizeBufferName, UseClassName = false)]
        public static SizeBufferData SizeBuffer;

        [Layout(Set = 0, Binding = 1, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(DataBufferName, UseClassName = false)]
        public static NetworkDataBuffer DataBuffer;

        [Layout(Set = 0, Binding = 2, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(ActivationBufferName, UseClassName = false)]
        public static NetworkDataBuffer ActivationBuffer;

        [Layout(Set = 0, Binding = 3, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(PreSigmoidBufferName, UseClassName = false)]
        public static NetworkDataBuffer PreSigmoidBuffer;

        [Layout(Set = 0, Binding = 4, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(DeltaBufferName, UseClassName = false)]
        public static NetworkDataBuffer DeltaBuffer;
    }
}