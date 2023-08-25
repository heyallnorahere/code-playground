using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
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

        [Layout(Set = 0, Binding = 0, ResourceType = ShaderResourceType.Uniform)]
        [ShaderFieldName(SizeBufferName, UseClassName = false)]
        public static SizeBufferData SizeBuffer;

        [Layout(Set = 0, Binding = 1, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(DataBufferName, UseClassName = false)]
        public static NetworkDataBuffer DataBuffer;

        [Layout(Set = 0, Binding = 2, ResourceType = ShaderResourceType.Storage)]
        [ShaderFieldName(ActivationBufferName, UseClassName = false)]
        public static NetworkDataBuffer ActivationBuffer;
    }
}