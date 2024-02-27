using CodePlayground;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Linq;

namespace MachineLearning.Noise
{
    public interface IDatasetNoise
    {
        public void ImGuiSettings();
        public float[] Apply(Random random, float[] data);
    }

    public sealed class StaticNoise : IDatasetNoise
    {
        public StaticNoise()
        {
            mMaxNoise = 0.02f;
        }

        public void ImGuiSettings()
        {
            ImGui.SliderFloat("Noise strength", ref mMaxNoise, 0f, 0.2f);
        }

        public float[] Apply(Random random, float[] data)
        {
            using var applyEvent = Profiler.Event();
            // todo: make hardware accelerated e.g. compute shader
            // though honestly that might not be feasible due to System.Random

            var result = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                float value = data[i];
                value += mMaxNoise * random.NextSingle();
                result[i] = float.Min(value, 1f);
            }

            return result;
        }

        private float mMaxNoise;
    }

    public sealed class RotationNoise : IDatasetNoise
    {
        public RotationNoise(int imageWidth, int imageHeight)
        {
            mImageWidth = imageWidth;
            mImageHeight = imageHeight;
            mMaxAngle = 6f * MathF.PI / 180f;
        }

        public void ImGuiSettings()
        {
            ImGui.SliderAngle("Max image rotation", ref mMaxAngle);
        }

        public float[] Apply(Random random, float[] data)
        {
            using var applyEvent = Profiler.Event();
            // oh god...
            // todo: make hardware accelerated e.g. framebuffer
            // very slow lmao (average 57.76 microseconds on dev machine according to tracy)

            var pixels = data.Select(pixel => new L8((byte)(pixel * byte.MaxValue))).ToArray();
            var image = Image.LoadPixelData<L8>(pixels, mImageWidth, mImageHeight);

            float angle = mMaxAngle * 2f * (random.NextSingle() - 0.5f);
            image.Mutate(x => x.Rotate(angle * 180f / MathF.PI).Crop(mImageWidth, mImageHeight));

            image.CopyPixelDataTo(pixels);
            return pixels.Select(pixel => (float)pixel.PackedValue / byte.MaxValue).ToArray();
        }

        private readonly int mImageWidth, mImageHeight;
        private float mMaxAngle;
    }
}
