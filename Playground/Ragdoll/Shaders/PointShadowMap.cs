using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace Ragdoll.Shaders
{
    [CompiledShader]
    public sealed class PointShadowMap
    {
        public struct FragmentIn
        {
            [Layout(Location = 0)]
            public Vector3<float> FragmentPosition;
        }

        public struct VertexOut
        {
            [ShaderVariable(ShaderVariableID.OutputPosition)]
            public Vector4<float> OutputPosition;
            public FragmentIn Data;
        }

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(ModelShader.VertexIn input)
        {
            int lightIndex = ModelShader.u_PushConstants.LightIndex;
            int faceIndex = ModelShader.u_PushConstants.FaceIndex;

            var shadowMatrix = ModelShader.u_LightBuffer.PointLights[lightIndex].ShadowMatrices[faceIndex];
            var model = ModelShader.CalculateModelMatrix(input.BoneCount, input.BoneIDs, input.BoneWeights);

            var worldPosition = model * new Vector4<float>(input.Position, 1f);
            return new VertexOut
            {
                OutputPosition = shadowMatrix * worldPosition,
                Data = new FragmentIn
                {
                    FragmentPosition = worldPosition.XYZ
                }
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: ShaderVariable(ShaderVariableID.FragmentDepth)]
        public static float FragmentMain(FragmentIn input)
        {
            var lightPosition = ModelShader.u_LightBuffer.PointLights[ModelShader.u_PushConstants.LightIndex].Position;
            float lightDistance = (input.FragmentPosition - lightPosition).Length();
            return lightDistance / ModelShader.u_LightBuffer.FarPlane;
        }
    }
}
