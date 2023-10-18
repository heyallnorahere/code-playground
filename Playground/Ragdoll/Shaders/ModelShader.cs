using System.Numerics;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Shaders;

namespace Ragdoll.Shaders
{
    [CompiledShader]
    public sealed class ModelShader
    {
        public struct VertexIn
        {
            [Layout(Location = 0)]
            public Vector3<float> Position;
            [Layout(Location = 1)]
            public Vector3<float> Normal;
            [Layout(Location = 2)]
            public Vector2<float> UV;
            [Layout(Location = 3)]
            public Vector3<float> Tangent;
            [Layout(Location = 4)]
            public int BoneCount;
            [Layout(Location = 5)]
            public Vector4<int> BoneIDs;
            [Layout(Location = 6)]
            public Vector4<float> BoneWeights;
        }

        public struct VertexOut
        {
            [ShaderVariable(ShaderVariableID.OutputPosition)]
            public Vector4<float> Position;
            public FragmentIn Data;
        }

        public struct FragmentIn
        {
            [Layout(Location = 0)]
            public Vector3<float> Normal;
            [Layout(Location = 1)]
            public Vector2<float> UV;
            [Layout(Location = 2)]
            public Vector3<float> WorldPosition;
            [Layout(Location = 3)]
            public Vector3<float> TBN_0;
            [Layout(Location = 4)]
            public Vector3<float> TBN_1;
            [Layout(Location = 5)]
            public Vector3<float> TBN_2;
        }

        public struct CameraBufferData
        {
            public Matrix4x4<float> Projection, View;
            public Vector3<float> Position;
        }

        public const int MaxBones = 100;
        public const int MaxInstances = 50;
        public const int MaxPointLights = 16;

        public struct BoneBufferData
        {
            [ArraySize(MaxBones * MaxInstances)]
            public Matrix4x4<float>[] BoneTransforms;
        }

        public struct PushConstantData
        {
            public Matrix4x4<float> Model;
            public int BoneOffset;
            public int EntityIndex;

            // hacky workaround for vulkan memory layouts
            public int LightIndex;
        }

        public struct MaterialBufferData
        {
            public Vector3<float> DiffuseColor, SpecularColor, AmbientColor;
            public float Shininess, Opacity;
            public bool HasNormalMap;
        }

        public struct AttenuationData
        {
            public float Quadratic, Linear, Constant;
        }

        public const int CubemapFaceCount = 6;
        public struct PointLightData
        {
            public Vector3<float> Diffuse, Specular, Ambient;
            public Vector3<float> Position;
            public AttenuationData Attenuation;
            public int EntityIndex;

            [ArraySize(CubemapFaceCount)]
            public Matrix4x4<float>[] ShadowMatrices;
        }

        public struct LightBufferData
        {
            [ArraySize(MaxPointLights)]
            public PointLightData[] PointLights;
            public int PointLightCount;

            public float FarPlane, Bias;
            public int SampleCount;
            [ArraySize(32)]
            public Vector3<float>[] SampleOffsetDirections;

            public Matrix4x4<float> Projection;
        }

        private struct MaterialColorData
        {
            public Vector3<float> Diffuse, Specular, Ambient;
        }

        [Layout(PushConstant = true)]
        public static PushConstantData u_PushConstants;

        [Layout(Set = 0, Binding = 0)]
        public static CameraBufferData u_CameraBuffer;
        [Layout(Set = 0, Binding = 1)]
        public static BoneBufferData u_BoneBuffer;

        [Layout(Set = 0, Binding = 2)]
        public static LightBufferData u_LightBuffer;
        [Layout(Set = 0, Binding = 3)]
        [ArraySize(MaxPointLights)]
        public static SamplerCube<float>[]? u_PointShadowMaps;

        [Layout(Set = 1, Binding = 0)]
        public static MaterialBufferData u_MaterialBuffer;
        [Layout(Set = 1, Binding = 1)]
        public static Sampler2D<float>? u_DiffuseMap;
        [Layout(Set = 1, Binding = 2)]
        public static Sampler2D<float>? u_SpecularMap;
        [Layout(Set = 1, Binding = 3)]
        public static Sampler2D<float>? u_AmbientMap;
        [Layout(Set = 1, Binding = 4)]
        public static Sampler2D<float>? u_NormalMap;

        public static Matrix4x4<float> CalculateModelMatrix(int boneCount, Vector4<int> boneIds, Vector4<float> boneWeights)
        {
            var transform = u_PushConstants.Model;
            if (boneCount > 0)
            {
                var boneTransform = new Matrix4x4<float>(0f);
                for (int i = 0; i < boneCount; i++)
                {
                    int transformIndex = u_PushConstants.BoneOffset + boneIds[i];
                    boneTransform += u_BoneBuffer.BoneTransforms[transformIndex] * boneWeights[i];
                }

                transform *= boneTransform;
            }

            return transform;
        }

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            var model = CalculateModelMatrix(input.BoneCount, input.BoneIDs, input.BoneWeights);

            var vertexPosition = new Vector4<float>(input.Position, 1f);
            var worldPosition = model * vertexPosition;

            var normalMatrix = new Matrix3x3<float>(model).Inverse().Transpose();
            var normal = (normalMatrix * input.Normal).Normalize();
            var tangent = (normalMatrix * input.Tangent).Normalize();
            var bitangent = BuiltinFunctions.Cross(normal, tangent).Normalize();
            var tbn = new Matrix3x3<float>(tangent, bitangent, normal);

            return new VertexOut
            {
                Position = u_CameraBuffer.Projection * u_CameraBuffer.View * worldPosition,
                Data = new FragmentIn
                {
                    Normal = normal,
                    UV = input.UV,
                    WorldPosition = worldPosition.XYZ,
                    TBN_0 = tbn[0],
                },
            };
        }

        private static float CalculateAttenuation(AttenuationData attenuation, float distance)
        {
            // ax^2 + bx + c
            // inverse square law or something
            float quadraticTerm = attenuation.Quadratic * distance * distance;
            float linearTerm = attenuation.Linear * distance;
            return 1f / (quadraticTerm + linearTerm + attenuation.Constant);
        }

        private static float CalculateDiffuse(Vector3<float> normal, Vector3<float> direction)
        {
            // cosine of the angle between the two vectors
            // dot of a and b is length(a) * length(b) * cos(theta)
            // these two vectors are both normals (magnitude of 1)
            return BuiltinFunctions.Max(BuiltinFunctions.Dot(normal, direction), 0f);
        }

        private static float CalculateSpecular(Vector3<float> normal, Vector3<float> lightDirection, Vector3<float> worldPosition)
        {
            var viewDirection = (u_CameraBuffer.Position - worldPosition).Normalize();
            var lightReflection = BuiltinFunctions.Reflect(-lightDirection, normal).Normalize();
            return BuiltinFunctions.Pow(BuiltinFunctions.Max(BuiltinFunctions.Dot(viewDirection, lightReflection), 0f), u_MaterialBuffer.Shininess);
        }

        private static float PointShadowCalculation(Vector3<float> worldPosition, int light)
        {
            var lightPosition = u_LightBuffer.PointLights[light].Position;
            var positionDifference = worldPosition - lightPosition;

            float currentDepth = positionDifference.Length();
            float viewDistance = (u_CameraBuffer.Position - worldPosition).Length();
            float diskRadius = (viewDistance / u_LightBuffer.FarPlane + 1f) / 25f;

            float shadow = 0f;
            for (int i = 0; i < u_LightBuffer.SampleCount; i++)
            {
                var uvw = positionDifference + u_LightBuffer.SampleOffsetDirections[i] * diskRadius;
                float closestDepth = u_PointShadowMaps![light].Sample(uvw).R * u_LightBuffer.FarPlane;

                if (currentDepth - u_LightBuffer.Bias > closestDepth)
                {
                    shadow += 1f;
                }
            }

            return shadow / u_LightBuffer.SampleCount;
        }

        private static Vector3<float> CalculatePointLight(int light, Vector3<float> normal, Vector3<float> worldPosition, MaterialColorData colorData)
        {
            var lightData = u_LightBuffer.PointLights[light];

            var lightPositionDifference = lightData.Position - worldPosition;
            var lightDirection = lightPositionDifference.Normalize();
            float distance = lightPositionDifference.Length();

            float diffuseStrength, specularStrength;
            if (lightData.EntityIndex == u_PushConstants.EntityIndex)
            {
                diffuseStrength = specularStrength = 1f;
            }
            else
            {
                diffuseStrength = CalculateDiffuse(normal, lightDirection);
                specularStrength = CalculateSpecular(normal, lightDirection, worldPosition);
            }

            var diffuse = colorData.Diffuse * lightData.Diffuse * diffuseStrength;
            var specular = colorData.Specular * lightData.Specular * specularStrength;
            var ambient = colorData.Ambient * lightData.Ambient;

            float shadowFactor = 1f - PointShadowCalculation(worldPosition, light);
            var directLight = shadowFactor * (diffuse + specular);
            var aggregate = directLight + ambient;

            return aggregate * CalculateAttenuation(lightData.Attenuation, distance);
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain(FragmentIn input)
        {
            Vector3<float> normal;
            if (u_MaterialBuffer.HasNormalMap)
            {
                var tbn = new Matrix3x3<float>(input.TBN_0, input.TBN_1, input.TBN_2);

                var sampledNormal = (u_NormalMap!.Sample(input.UV).XYZ * 2f - 1f).Normalize();
                normal = (tbn * sampledNormal).Normalize();
            }
            else
            {
                normal = input.Normal.Normalize();
            }

            var aggregate = new Vector3<float>(0f);
            var colorData = new MaterialColorData
            {
                Diffuse = u_DiffuseMap!.Sample(input.UV).XYZ * u_MaterialBuffer.DiffuseColor,
                Specular = u_SpecularMap!.Sample(input.UV).XYZ * u_MaterialBuffer.SpecularColor,
                Ambient = u_SpecularMap!.Sample(input.UV).XYZ * u_MaterialBuffer.AmbientColor
            };

            for (int i = 0; i < u_LightBuffer.PointLightCount; i++)
            {
                aggregate += CalculatePointLight(i, normal, input.WorldPosition, colorData);
            }

            return new Vector4<float>(aggregate, u_MaterialBuffer.Opacity);
        }
    }
}