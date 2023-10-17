using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace Ragdoll.Shaders
{
    [CompiledShader]
    public sealed class PointShadowMap
    {
        public struct VertexOut
        {
            [ShaderVariable(ShaderVariableID.OutputPosition)]
            public Vector4<float> OutputPosition;
        }

        [ShaderVariable(ShaderVariableID.CubemapLayer)]
        private static int s_CubemapLayer;
        [ShaderVariable(ShaderVariableID.OutputPosition)]
        private static Vector4<float>? s_OutputPosition;
        [Layout(Location = 0)]
        private static Vector3<float>? s_FragmentPosition;

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(ModelShader.VertexIn input)
        {
            var model = ModelShader.CalculateModelMatrix(input.BoneCount, input.BoneIDs, input.BoneWeights);
            return new VertexOut
            {
                OutputPosition = model * new Vector4<float>(input.Position, 1f),
            };
        }

        [ShaderEntrypoint(ShaderStage.Geometry)]
        [GeometryPrimitives(GeometryInputPrimitive.Triangles, GeometryOutputPrimitive.TriangleStrip, ModelShader.CubemapFaceCount * 3)]
        public static void GeometryMain([ShaderVariable(ShaderVariableID.GeometryInput)] VertexOut[] input)
        {
            int lightIndex = ModelShader.u_PushConstants.LightIndex;
            var lightData = ModelShader.u_LightBuffer.PointLights[lightIndex];

            for (int i = 0; i < ModelShader.CubemapFaceCount; i++)
            {
                s_CubemapLayer = i;

                for (int j = 0; j < 3; j++)
                {
                    var shadowMatrix = lightData.ShadowMatrices[j];
                    var fragmentPosition = shadowMatrix * input[j].OutputPosition;

                    s_FragmentPosition = fragmentPosition.XYZ;
                    s_OutputPosition = fragmentPosition;

                    BuiltinFunctions.EmitVertex();
                }

                BuiltinFunctions.EndPrimitive();
            }
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: ShaderVariable(ShaderVariableID.FragmentDepth)]
        public static float FragmentMain()
        {
            var lightPosition = ModelShader.u_LightBuffer.PointLights[ModelShader.u_PushConstants.LightIndex].Position;
            float lightDistance = (s_FragmentPosition! - lightPosition).Length();
            return lightDistance / ModelShader.u_LightBuffer.FarPlane;
        }
    }
}
