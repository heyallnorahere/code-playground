using LibChess;
using MachineLearning;
using Optick.NET;
using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZstdNet;

namespace ChessAI.Data
{
    public sealed class PositionData
    {
        public PositionData()
        {
            Position = string.Empty;
            PieceMoved = string.Empty;
            BestMove = string.Empty;
            Promotion = PieceType.None;
        }

        [PrimaryKey, Indexed]
        public int ID { get; set; }

        [Unique]
        public string Position { get; set; }
        public string PieceMoved { get; set; }
        public string BestMove { get; set; }
        public PieceType Promotion { get; set; }
    }

    public sealed class Dataset : IDataset, IDisposable
    {
        public const int NetworkInputCount = Board.Width * Board.Width + 3; // data in a FEN string
        public const int NetworkOutputCount = Board.Width * 4 + 4; // rank and file, source and destination, plus promotion piece types

        private static readonly HashSet<string?> sSubmittedFENs;
        static Dataset()
        {
            sSubmittedFENs = new HashSet<string?>();
        }

        public static async Task PullAndLabelAsync(Dataset dataset, string command, int year, int month, int depth)
        {
            using var pullAndLabelEvent = OptickMacros.Event();
            string url = $"https://database.lichess.org/standard/lichess_db_standard_rated_{year:####}-{month:0#}.pgn.zst";

            const string cacheDirectory = "cache";
            string plaintextCache = $"{cacheDirectory}/lichess_{year:####}_{month:0#}.pgn";
            string compressedCache = $"{plaintextCache}.zst";

            if (!File.Exists(plaintextCache))
            {
                using var decompressEvent = OptickMacros.Event("Decompress database");
                if (!File.Exists(compressedCache))
                {
                    using var pullEvent = OptickMacros.Event("Pull database");
                    if (!Directory.Exists(cacheDirectory))
                    {
                        Directory.CreateDirectory(cacheDirectory);
                    }

                    Console.WriteLine($"Pulling database from {url}");
                    using var client = new HttpClient
                    {
                        Timeout = Timeout.InfiniteTimeSpan,
                        MaxResponseContentBufferSize = int.MaxValue
                    };

                    var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    using var responseStream = await response.Content.ReadAsStreamAsync();

                    Console.WriteLine($"Writing to compressed cache file {compressedCache}");
                    using var cacheStream = new FileStream(compressedCache, FileMode.Create, FileAccess.Write);
                    responseStream.CopyTo(cacheStream);
                }

                Console.WriteLine($"Reading from compressed cache file {compressedCache}");
                using var compressedStream = new FileStream(compressedCache, FileMode.Open, FileAccess.Read);

                Console.WriteLine($"Decompressing & writing {compressedStream.Length} bytes...");
                using var zstdStream = new DecompressionStream(compressedStream);
                using var decompressedCacheStream = new FileStream(plaintextCache, FileMode.Create, FileAccess.Write);
                zstdStream.CopyTo(decompressedCacheStream);
            }

            using var decompressedStream = new FileStream(plaintextCache, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(decompressedStream);

            using var pond = new Pond(command, depth);
            pond.PositionDigested += digestion =>
            {
                lock (sSubmittedFENs)
                {
                    sSubmittedFENs.Remove(digestion.Position);
                }

                if (digestion.BestMove is null)
                {
                    return;
                }

                var moveData = digestion.BestMove.Value;
                lock (dataset)
                {
                    dataset.AddEntry(new PositionData
                    {
                        ID = dataset.mCount,
                        Position = digestion.Position,
                        PieceMoved = moveData.Move.Position.ToString(),
                        BestMove = moveData.Move.Destination.ToString(),
                        Promotion = moveData.Promotion
                    });
                }
            };

            int skipped;
            using (OptickMacros.Event("Split & process PGN file"))
            {
                // start the stockfish worker thread
                pond.Start();

                Console.WriteLine($"Splitting & processing PGN file... ({decompressedStream.Length} bytes)");
                skipped = await PGN.SplitAsync(reader, pgn => LabelPGN(dataset, pond, pgn), true);

                // stop and wait for it to finish
                pond.Stop(true);
            }

            Console.WriteLine("Finished labeling dataset");
            Console.WriteLine($"{skipped} total games skipped due to errors");
        }

        public static void LabelPGN(Dataset dataset, Pond pond, PGN pgn)
        {
            FeedPond(dataset, pond, null);
            foreach (var move in pgn.Moves)
            {
                FeedPond(dataset, pond, move.Position);
            }
        }

        private static void FeedPond(Dataset dataset, Pond pond, string? fen)
        {
            lock (dataset)
            {
                if (dataset.Contains(fen ?? Pond.DefaultFEN))
                {
                    return;
                }
            }

            lock (sSubmittedFENs)
            {
                if (!sSubmittedFENs.Add(fen))
                {
                    return;
                }
            }

            pond.Feed(fen);
        }

        public Dataset(string path)
        {
            using var constructorEvent = OptickMacros.Event();

            mConnection = new SQLiteConnection(path);
            mConnection.CreateTable<PositionData>();
            mCount = mConnection.Table<PositionData>().Count();
        }

        public void Dispose() => mConnection.Dispose();

        public void AddEntry(PositionData data)
        {
            using var addEntryEvent = OptickMacros.Event();

            mConnection.Insert(data);
            mCount++;
        }

        public bool Contains(string position)
        {
            using var containsEvent = OptickMacros.Event();
            return mConnection.Find<PositionData>(data => data.Position == position) is not null;
        }

        public int Count => mCount;
        public int InputCount => NetworkInputCount;
        public int OutputCount => NetworkOutputCount;

        public float[] GetInput(int index)
        {
            using var getInputEvent = OptickMacros.Event();

            var data = mConnection.Table<PositionData>().ElementAt(index);
            using var board = Board.Create(data.Position);

            if (board is null)
            {
                throw new InvalidOperationException("Failed to interpret FEN!");
            }

            return board.GetNetworkInput();
        }

        public float[] GetExpectedOutput(int index)
        {
            using var getOutputEvent = OptickMacros.Event();
            var data = mConnection.Table<PositionData>().ElementAt(index);

            var outputs = new float[NetworkOutputCount];
            Array.Fill(outputs, 0f);

            var piecePosition = Coord.Parse(data.PieceMoved);
            var bestMove = Coord.Parse(data.BestMove);

            outputs[piecePosition.X] = 1f;
            outputs[Board.Width + piecePosition.Y] = 1f;
            outputs[Board.Width * 2 + bestMove.X] = 1f;
            outputs[Board.Width * 3 + bestMove.Y] = 1f;

            var promotion = data.Promotion;
            if (promotion != PieceType.None)
            {
                outputs[Board.Width * 4 + (int)promotion - 2] = 1f;
            }

            return outputs;
        }

        private readonly SQLiteConnection mConnection;
        private int mCount;
    }
}
