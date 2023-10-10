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
            public Matrix3x3<float> TBN;
        }

        public struct CameraBufferData
        {
            public Matrix4x4<float> Projection, View;
        }

        public const int MaxBones = 100;
        public const int MaxInstances = 50;

        public struct BoneBufferData
        {
            [ArraySize(MaxBones * MaxInstances)]
            public Matrix4x4<float>[] BoneTransforms;
        }

        public struct PushConstantData
        {
            public Matrix4x4<float> Model;
            public int BoneOffset;
        }

        public struct MaterialBufferData
        {
            public Vector3<float> DiffuseColor, SpecularColor, AmbientColor;
            public float Shininess, Opacity;
            public bool HasNormalMap;
        }

        [Layout(Set = 0, Binding = 0)]
        public static CameraBufferData u_CameraBuffer;
        [Layout(Set = 0, Binding = 1)]
        public static BoneBufferData u_BoneBuffer;
        [Layout(PushConstant = true)]
        public static PushConstantData u_PushConstants;

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

        [ShaderEntrypoint(ShaderStage.Vertex)]
        public static VertexOut VertexMain(VertexIn input)
        {
            var transform = u_PushConstants.Model;
            if (input.BoneCount > 0)
            {
                var boneTransform = new Matrix4x4<float>(0f);
                for (int i = 0; i < input.BoneCount; i++)
                {
                    int transformIndex = u_PushConstants.BoneOffset + input.BoneIDs[i];
                    boneTransform += u_BoneBuffer.BoneTransforms[transformIndex] * input.BoneWeights[i];
                }
                
                transform *= boneTransform;
            }

            var vertexPosition = new Vector4<float>(input.Position, 1f);
            var worldPosition = transform * vertexPosition;

            var normalMatrix = new Matrix3x3<float>(transform).Inverse().Transpose();
            var normal = (normalMatrix * input.Normal).Normalize();
            var tangent = (normalMatrix * input.Tangent).Normalize();
            var bitangent = BuiltinFunctions.Cross(normal, tangent).Normalize();

            return new VertexOut
            {
                Position = u_CameraBuffer.Projection * u_CameraBuffer.View * worldPosition,
                Data = new FragmentIn
                {
                    Normal = normal,
                    UV = input.UV,
                    TBN = new Matrix3x3<float>(tangent, bitangent, normal)
                },
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain(FragmentIn input)
        {
            Vector3<float> normal;
            if (u_MaterialBuffer.HasNormalMap)
            {
                var sampledNormal = (u_NormalMap!.Sample(input.UV).XYZ * 2f - 1f).Normalize();
                normal = (input.TBN * sampledNormal).Normalize();
            }
            else
            {
                normal = input.Normal.Normalize();
            }

            var sample = u_DiffuseMap!.Sample(input.UV);
            var color = new Vector4<float>(BuiltinFunctions.Lerp(u_MaterialBuffer.DiffuseColor, normal, 0.5f), u_MaterialBuffer.Opacity);
            return sample * color;
        }
    }
}