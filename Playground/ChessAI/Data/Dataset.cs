using LibChess;
using MachineLearning;
using Newtonsoft.Json;
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

    public sealed class NetworkInputData
    {
        public NetworkInputData()
        {
            Position = string.Empty;
            NetworkInput = string.Empty;
        }

        [PrimaryKey, Unique]
        public string Position { get; set; }
        public string NetworkInput { get; set; }
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
                    dataset.mConnection.Insert(new PositionData
                    {
                        ID = dataset.mCount++,
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
                FeedPond(dataset, pond, move);
            }
        }

        private static void FeedPond(Dataset dataset, Pond pond, PGNMove? move)
        {
            string usedFen = move?.Position ?? Pond.DefaultFEN;
            lock (dataset)
            {
                if (dataset.mConnection.Find<NetworkInputData>(usedFen) is null)
                {
                    float[] networkInput;
                    if (move is null)
                    {
                        using var board = Board.Create();
                        networkInput = board.GetNetworkInput();
                    }
                    else
                    {
                        networkInput = move.Value.NetworkInput;
                    }

                    dataset.mConnection.Insert(new NetworkInputData
                    {
                        Position = usedFen,
                        NetworkInput = JsonConvert.SerializeObject(networkInput, Formatting.None)
                    });
                }

                if (dataset.mConnection.Find<PositionData>(data => data.Position == usedFen) is not null)
                {
                    return;
                }
            }

            lock (sSubmittedFENs)
            {
                if (!sSubmittedFENs.Add(move?.Position))
                {
                    return;
                }
            }

            pond.Feed(move?.Position);
        }

        public Dataset(string path)
        {
            using var constructorEvent = OptickMacros.Event();

            mConnection = new SQLiteConnection(path);
            mConnection.CreateTable<PositionData>();
            mConnection.CreateTable<NetworkInputData>();

            mCount = mConnection.Table<PositionData>().Count();
        }

        public void Dispose() => mConnection.Dispose();

        public int Count => mCount;
        public int InputCount => NetworkInputCount;
        public int OutputCount => NetworkOutputCount;

        public float[] GetInput(int index)
        {
            using var getInputEvent = OptickMacros.Event();

            var data = mConnection.Table<PositionData>().ElementAt(index);
            var networkInput = mConnection.Find<NetworkInputData>(data.Position);

            if (networkInput is null)
            {
                using var board = Board.Create(data.Position);
                if (board is null)
                {
                    throw new ArgumentException("Invalid FEN string!");
                }

                return board.GetNetworkInput();
            }

            var input = JsonConvert.DeserializeObject<float[]>(networkInput.NetworkInput);
            if (input is null)
            {
                throw new ArgumentException("Failed to deserialize network input!");
            }

            return input;
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
                // subtracting 2 (magic number) because that is the enum offset of the queen
                // promotion is assumed to be within the range of 2..5 because they are the only legal pieces to promote to
                outputs[Board.Width * 4 + (int)promotion - 2] = 1f;
            }

            return outputs;
        }

        private readonly SQLiteConnection mConnection;
        private int mCount;
    }
}
