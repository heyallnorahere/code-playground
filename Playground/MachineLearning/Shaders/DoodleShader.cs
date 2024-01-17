using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace MachineLearning.Shaders
{
    public enum DoodleTool
    {
        Brush,
        Eraser,
        Clear
    }

    [CompiledShader]
    public sealed class DoodleShader
    {
        public const int KernelSize = 32;

        public struct PushConstants
        {
            public Vector2<uint> Position, ImageSize;
            public DoodleTool Tool;
            public float ToolSize;
        }

        [Layout(Set = 0, Binding = 0, Format = ShaderImageFormat.R8)]
        public Image2D<float>? u_Result;

        [Layout(PushConstant = true)]
        public PushConstants u_PushConstants;

        [ShaderEntrypoint(ShaderStage.Compute)]
        [NumThreads(KernelSize, KernelSize, 1)]
        public void ComputeMain([ShaderVariable(ShaderVariableID.GlobalInvocationID)] Vector3<uint> invocation)
        {
            var kernelPosition = new Vector2<float>(invocation.X, invocation.Y);
            var toolPosition = new Vector2<float>(u_PushConstants.Position.X, u_PushConstants.Position.Y);

            float distance = (kernelPosition - toolPosition).Length();
            float strength = BuiltinFunctions.Pow((u_PushConstants.ToolSize - distance) / u_PushConstants.ToolSize, 3);
            float normalizedStrength = BuiltinFunctions.Max(strength, 0f);

            var position = new Vector2<int>((int)invocation.X, (int)invocation.Y);
            var value = u_Result!.Load(position);

            if (u_PushConstants.Tool == DoodleTool.Brush)
            {
                value += new Vector4<float>(normalizedStrength);
            }
            else if (u_PushConstants.Tool == DoodleTool.Eraser)
            {
                value = BuiltinFunctions.Lerp(value, new Vector4<float>(0f), normalizedStrength);
            }
            else if (u_PushConstants.Tool == DoodleTool.Clear)
            {
                value = new Vector4<float>(0f);
            }

            u_Result!.Store(position, value);
        }
    }
}
