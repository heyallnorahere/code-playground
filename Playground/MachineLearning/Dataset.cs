using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

// dataset is pulled from here https://yann.lecun.com/exdb/mnist/
namespace MachineLearning
{
    internal struct LabeledImage
    {
        public float[,] Image;
        public int Label;
    }

    internal sealed class Dataset
    {
        private static int ReadInt32WithEndianness(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(Marshal.SizeOf<int>());
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToInt32(bytes);
        }

        private static float[,,] ReadImageData(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (ReadInt32WithEndianness(reader) != 0x803)
            {
                throw new IOException("Invalid magic number!");
            }

            int imageCount = ReadInt32WithEndianness(reader);
            int rowCount = ReadInt32WithEndianness(reader);
            int columnCount = ReadInt32WithEndianness(reader);

            var result = new float[imageCount, rowCount, columnCount];
            for (int i = 0; i < imageCount; i++)
            {
                for (int y = 0; y < rowCount; y++)
                {
                    for (int x = 0; x < columnCount; x++)
                    {
                        byte value = reader.ReadByte();
                        result[i, x, y] = (float)value / byte.MaxValue;
                    }
                }
            }

            return result;
        }

        private static int[] ReadLabelData(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (ReadInt32WithEndianness(reader) != 0x801)
            {
                throw new IOException("Invalid magic number!");
            }

            int labelCount = ReadInt32WithEndianness(reader);
            var result = new int[labelCount];
            for (int i = 0; i < labelCount; i++)
            {
                result[i] = reader.ReadByte();
            }

            return result;
        }

        public static Dataset Load(Stream imageStream, Stream labelStream)
        {
            var imageData = ReadImageData(imageStream);
            var labelData = ReadLabelData(labelStream);

            int count = imageData.GetLength(0);
            int width = imageData.GetLength(1);
            int height = imageData.GetLength(2);

            if (count != labelData.Length)
            {
                throw new ArgumentException("Dataset size mismatch!");
            }

            var data = new LabeledImage[count];
            for (int i = 0; i < count; i++)
            {
                var image = new LabeledImage
                {
                    Image = new float[width, height],
                    Label = labelData[i]
                };

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        image.Image[x, y] = imageData[i, x, y];
                    }
                }

                data[i] = image;
            }

            return new Dataset(data);
        }

        private static async Task<Stream> PullAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var compressed = await response.Content.ReadAsStreamAsync();
            return new GZipStream(compressed, CompressionMode.Decompress);
        }

        // hack hacky hack hack
        public static Dataset Pull(string imageUrl, string labelUrl)
        {
            using var client = new HttpClient();
            var imageTask = PullAsync(client, imageUrl);
            var labelTask = PullAsync(client, labelUrl);

            Task.WhenAll(imageTask, labelTask).Wait();
            using (imageTask.Result)
            {
                using (labelTask.Result)
                {
                    return Load(imageTask.Result, labelTask.Result);
                }
            }
        }

        public Dataset(IReadOnlyList<LabeledImage> data)
        {
            int width = -1;
            int height = -1;
            int count = data.Count;

            mData = new LabeledImage[count];
            for (int i = 0; i < count; i++)
            {
                var existingImage = data[i];
                int existingWidth = existingImage.Image.GetLength(0);
                int existingHeight = existingImage.Image.GetLength(1);

                if (width < 0)
                {
                    width = existingWidth;
                }
                else if (width != existingWidth)
                {
                    throw new ArgumentException("Inconsistent width!");
                }

                if (height < 0)
                {
                    height = existingHeight;
                }
                else if (height != existingHeight)
                {
                    throw new ArgumentException("Inconsistent height!");
                }

                var image = new LabeledImage
                {
                    Image = new float[width, height],
                    Label = existingImage.Label
                };

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        image.Image[x, y] = existingImage.Image[x, y];
                    }
                }

                mData[i] = image;
            }

            mWidth = width;
            mHeight = height;
            mCount = count;
        }

        public float[] GetInput(int index)
        {
            var input = new float[mWidth * mHeight];
            var data = mData[index].Image;

            for (int x = 0; x < mWidth; x++)
            {
                for (int y = 0; y < mHeight; y++)
                {
                    input[y * mWidth + x] = data[x, y];
                }
            }

            return input;
        }

        public float[] GetExpectedOutput(int index)
        {
            var output = new float[10];
            int label = mData[index].Label;

            for (int i = 0; i < output.Length; i++)
            {
                output[i] = i == label ? 1f : 0f;
            }

            return output;
        }

        public byte[] GetImageData(int index, int channels)
        {
            var data = mData[index].Image;
            var result = new byte[mWidth * mHeight * channels];

            for (int x = 0; x < mWidth; x++)
            {
                for (int y = 0; y < mHeight; y++)
                {
                    int pixelOffset = (y * mWidth + x) * channels;
                    byte pixelValue = (byte)(data[x, y] * byte.MaxValue);

                    for (int i = 0; i < channels; i++)
                    {
                        result[pixelOffset + i] = i < 3 ? pixelValue : byte.MaxValue;
                    }
                }
            }

            return result;
        }

        public int Count => mCount;
        public int Width => mWidth;
        public int Height => mHeight;
        public int InputSize => mWidth * mHeight;

        private readonly int mWidth, mHeight, mCount;
        private readonly LabeledImage[] mData;
    }
}
