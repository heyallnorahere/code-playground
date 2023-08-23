using CodePlayground.Graphics.Shaders;

namespace VulkanTest.Shaders
{
    public static class ShaderUtilities
    {
        public static float ApplyKernel(Matrix3x3<float> kernel, Matrix3x3<float> data, float denominator, float offset)
        {
            float result = 0f;
            for (int i = 0; i < 3; i++)
            {
                var row = new Vector3<float>(0f);
                for (int j = 0; j < 3; j++)
                {
                    row[j] = kernel[j][i];
                }

                result += BuiltinFunctions.Dot(row, data[i]);
            }

            return BuiltinFunctions.Clamp(result / denominator + offset, 0f, 1f);
        }
    }
}
