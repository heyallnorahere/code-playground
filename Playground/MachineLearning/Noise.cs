using CodePlayground;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Linq;

namespace MachineLearning.Noise
{
    public interface IDatasetNoise
    {
        public float[] Apply(Random random, float[] data);
    }

    public sealed class StaticNoise : IDatasetNoise
    {
        public float[] Apply(Random random, float[] data)
        {
            using var applyEvent = Profiler.Event();
            // todo: make hardware accelerated e.g. compute shader
            // though honestly that might not be feasible due to System.Random

            var result = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                float value = data[i];
                value += 0.05f * random.NextSingle();
                result[i] = float.Min(value, 1f);
            }

            return result;
        }
    }

    public sealed class RotationNoise : IDatasetNoise
    {
        public RotationNoise(int imageWidth, int imageHeight)
        {
            mImageWidth = imageWidth;
            mImageHeight = imageHeight;
        }

        public float[] Apply(Random random, float[] data)
        {
            using var applyEvent = Profiler.Event();
            // oh god...
            // todo: make hardware accelerated e.g. framebuffer
            // very slow lmao (average 57.76 microseconds on dev machine according to tracy)

            var pixels = data.Select(pixel => new L8((byte)(pixel * byte.MaxValue))).ToArray();
            var image = Image.LoadPixelData<L8>(pixels, mImageWidth, mImageHeight);

            float angle = 6f * random.NextSingle();
            image.Mutate(x => x.Rotate(angle).Crop(mImageWidth, mImageHeight));

            image.CopyPixelDataTo(pixels);
            return pixels.Select(pixel => (float)pixel.PackedValue / byte.MaxValue).ToArray();
        }

        private readonly int mImageWidth, mImageHeight;
    }
}
