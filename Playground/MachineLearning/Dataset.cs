using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MachineLearning
{
    public enum DatasetGroup
    {
        Training,
        Testing,
        Evaluation
    }

    public interface IDataset
    {
        public int InputCount { get; }
        public int OutputCount { get; }

        public int GetGroupEntryCount(DatasetGroup group);
        public float[] GetInput(DatasetGroup group, int index);
        public float[] GetExpectedOutput(DatasetGroup group, int index);
    }

    public struct DatasetFileSource
    {
        public string Url, Cache;
    }

    // dataset is pulled from https://yann.lecun.com/exdb/mnist/
    public sealed class MNISTGroup
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

            var result = new float[imageCount, columnCount, rowCount];
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

        public static MNISTGroup Load(Stream imageStream, Stream labelStream)
        {
            var imageData = ReadImageData(imageStream);
            var labelData = ReadLabelData(labelStream);

            return new MNISTGroup(imageData, labelData);
        }

        private static async Task<Stream> PullAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var compressed = await response.Content.ReadAsStreamAsync();
            return new GZipStream(compressed, CompressionMode.Decompress);
        }

        // hack hacky hack hack
        // i dont care anymore
        public static MNISTGroup Load(DatasetFileSource images, DatasetFileSource labels)
        {
            Stream? imageStream = null;
            Stream? labelStream = null;

            using var client = new HttpClient();
            var tasks = new List<Task<Stream>>();
            Task<Stream>? imageTask, labelTask;

            if (File.Exists(images.Cache))
            {
                imageStream = new FileStream(images.Cache, FileMode.Open, FileAccess.Read);
                imageTask = null;
            }
            else
            {
                tasks.Add(imageTask = PullAsync(client, images.Url));
            }

            if (File.Exists(labels.Cache))
            {
                labelStream = new FileStream(labels.Cache, FileMode.Open, FileAccess.Read);
                labelTask = null;
            }
            else
            {
                tasks.Add(labelTask = PullAsync(client, labels.Url));
            }

            if (tasks.Count > 0)
            {
                Task.WhenAll(tasks).Wait();
            }

            if (imageStream is null)
            {
                imageStream = new MemoryStream();
                imageTask!.Result.CopyTo(imageStream);
                imageStream.Position = 0;

                var directory = Path.GetDirectoryName(images.Cache);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                using var cacheStream = new FileStream(images.Cache, FileMode.Create, FileAccess.Write);
                imageStream.CopyTo(cacheStream);
                imageStream.Position = 0;
            }

            if (labelStream is null)
            {
                labelStream = new MemoryStream();
                labelTask!.Result.CopyTo(labelStream);
                labelStream.Position = 0;

                var directory = Path.GetDirectoryName(labels.Cache);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                using var cacheStream = new FileStream(labels.Cache, FileMode.Create, FileAccess.Write);
                labelStream.CopyTo(cacheStream);
                labelStream.Position = 0;
            }

            using (imageStream)
            {
                using (labelStream)
                {
                    return Load(imageStream, labelStream);
                }
            }
        }

        public MNISTGroup(float[,,] images, int[] labels)
        {
            mCount = images.GetLength(0);
            mWidth = images.GetLength(1);
            mHeight = images.GetLength(2);

            if (labels.Length != mCount)
            {
                throw new ArgumentException("Dataset size mismatch!");
            }

            mImages = images;
            mLabels = labels;
        }

        public float[] GetInput(int index)
        {
            var input = new float[mWidth * mHeight];
            for (int x = 0; x < mWidth; x++)
            {
                for (int y = 0; y < mHeight; y++)
                {
                    input[y * mWidth + x] = mImages[index, x, y];
                }
            }

            return input;
        }

        public float[] GetExpectedOutput(int index)
        {
            var output = new float[10];
            int label = mLabels[index];

            for (int i = 0; i < output.Length; i++)
            {
                output[i] = i == label ? 1f : 0f;
            }

            return output;
        }

        public byte[] GetImageData(int index, int channels)
        {
            var result = new byte[mWidth * mHeight * channels];
            for (int x = 0; x < mWidth; x++)
            {
                for (int y = 0; y < mHeight; y++)
                {
                    int pixelOffset = (y * mWidth + x) * channels;
                    byte pixelValue = (byte)(mImages[index, x, y] * byte.MaxValue);

                    for (int i = 0; i < channels; i++)
                    {
                        result[pixelOffset + i] = i < 3 ? pixelValue : byte.MaxValue;
                    }
                }
            }

            return result;
        }

        public int Width => mWidth;
        public int Height => mHeight;

        public int Count => mCount;
        public int InputCount => mWidth * mHeight;
        public int OutputCount => 10;

        private readonly int mWidth, mHeight, mCount;
        private readonly int[] mLabels;
        private readonly float[,,] mImages;
    }

    public sealed class MNISTDatabase : IDataset
    {
        public MNISTDatabase()
        {
            mGroups = new Dictionary<DatasetGroup, MNISTGroup>();
            mWidth = mHeight = mOutputCount = -1;
        }

        public void SetGroup(DatasetGroup group, MNISTGroup data)
        {
            mGroups[group] = data;

            if (mWidth < 0)
            {
                mWidth = data.Width;
            }
            else if (mWidth != data.Width)
            {
                throw new ArgumentException("Mismatching image width!");
            }

            if (mHeight < 0)
            {
                mHeight = data.Height;
            }
            else if (mHeight != data.Height)
            {
                throw new ArgumentException("Mismatching image height!");
            }

            if (mOutputCount < 0)
            {
                mOutputCount = data.OutputCount;
            }
            else if (mOutputCount != data.OutputCount)
            {
                throw new ArgumentException("Mismatching output count!");
            }
        }

        public int Width => mWidth;
        public int Height => mHeight;
        public int InputCount => mWidth * mHeight;
        public int OutputCount => mOutputCount;

        public int GetGroupEntryCount(DatasetGroup group) => mGroups.GetValueOrDefault(group)?.Count ?? -1;
        public float[] GetInput(DatasetGroup group, int index) => mGroups[group].GetInput(index);
        public float[] GetExpectedOutput(DatasetGroup group, int index) => mGroups[group].GetExpectedOutput(index);
        public byte[] GetImageData(DatasetGroup group, int index, int channels) => mGroups[group].GetImageData(index, channels);

        private readonly Dictionary<DatasetGroup, MNISTGroup> mGroups;
        private int mWidth, mHeight, mOutputCount;
    }
}
