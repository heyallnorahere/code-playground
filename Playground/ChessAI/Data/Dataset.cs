using MachineLearning;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ChessAI.Data
{
    internal sealed class Dataset : IDataset
    {
        public static async Task PullAndLabelAsync(Dataset dataset, int year, int month)
        {
            string url = $"https://database.lichess.org/standard/lichess_db_standard_rated_{year:####}-{month:##}.pgn.zst";

            using var client = new HttpClient();
            using var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            // todo: zstd decompression
            using var reader = new StreamReader(stream);

            await PGN.SplitAsync(reader, pgn => LabelPGN(dataset, pgn));
        }

        private static void LabelPGN(Dataset dataset, PGN pgn)
        {
            // todo: labelling pgns
        }

        public Dataset(string path)
        {
            // todo: load sqlite database
        }

        public int Count => throw new NotImplementedException();
        public int InputCount => throw new NotImplementedException();
        public int OutputCount => throw new NotImplementedException();

        public float[] GetInput(int index) => throw new NotImplementedException();
        public float[] GetExpectedOutput(int index) => throw new NotImplementedException();
    }
}
