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
            public int BoneCount;
            [Layout(Location = 4)]
            public Vector4<int> BoneIDs;
            [Layout(Location = 5)]
            public Vector4<float> BoneWeights;
        }

        public struct VertexOut
        {
            [OutputPosition]
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
        }

        public struct CameraBufferData
        {
            public Matrix4x4<float> ViewProjection;
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
        }

        [Layout(Set = 0, Binding = 0)]
        public static CameraBufferData u_CameraBuffer;
        [Layout(Set = 0, Binding = 1)]
        public static BoneBufferData u_BoneBuffer;
        [Layout(PushConstants = true)]
        public static PushConstantData u_PushConstants;

        [Layout(Set = 1, Binding = 0)]
        public static MaterialBufferData u_MaterialBuffer;
        [Layout(Set = 1, Binding = 1)]
        public static Sampler2D<float>? u_DiffuseMap;
        [Layout(Set = 1, Binding = 2)]
        public static Sampler2D<float>? u_SpecularMap;
        [Layout(Set = 1, Binding = 2)]
        public static Sampler2D<float>? u_AmbientMap;

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

            return new VertexOut
            {
                Position = u_CameraBuffer.ViewProjection * worldPosition,
                Data = new FragmentIn
                {
                    Normal = (new Matrix3x3<float>(transform).Inverse().Transpose() * input.Normal).Normalize(),
                    UV = input.UV
                },
            };
        }

        [ShaderEntrypoint(ShaderStage.Fragment)]
        [return: Layout(Location = 0)]
        public static Vector4<float> FragmentMain(FragmentIn input)
        {
            // no shading for now
            // todo: THAT

            var sample = u_DiffuseMap!.Sample(input.UV);
            var color = new Vector4<float>(u_MaterialBuffer.DiffuseColor, u_MaterialBuffer.Opacity);
            return sample * color;
        }
    }
}