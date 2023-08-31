using LibChess;
using MachineLearning;
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

        [Unique, PrimaryKey]
        public string Position { get; set; }
        public string PieceMoved { get; set; }
        public string BestMove { get; set; }
        public PieceType Promotion { get; set; }
    }

    internal struct PGNData
    {
        public PGN PGN;
        public UCIEngine Engine;
        public Dataset Dataset;
        public int Depth;
    }

    public sealed class Dataset : IDataset, IDisposable
    {
        private static readonly AutoResetEvent sPGNAdded, sThreadStarted, sThreadFinished;
        private static readonly Queue<PGNData> sPGNQueue;
        private static Thread? sPGNThread;

        static Dataset()
        {
            sPGNAdded = new AutoResetEvent(false);
            sThreadStarted = new AutoResetEvent(false);
            sThreadFinished = new AutoResetEvent(false);
            sPGNQueue = new Queue<PGNData>();
            sPGNThread = null;
        }

        public static async Task PullAndLabelAsync(Dataset dataset, UCIEngine engine, int year, int month, int depth)
        {
            string url = $"https://database.lichess.org/standard/lichess_db_standard_rated_{year:####}-{month:0#}.pgn.zst";

            const string cacheDirectory = "cache";
            string plaintextCache = $"{cacheDirectory}/lichess_{year:####}_{month:0#}.pgn";
            string compressedCache = $"{plaintextCache}.zst";

            if (!File.Exists(plaintextCache))
            {
                Stream compressedStream;
                if (!File.Exists(compressedCache))
                {
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

                    var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    compressedStream = await response.Content.ReadAsStreamAsync();

                    using var cacheStream = new FileStream(compressedCache, FileMode.Create, FileAccess.Write);
                    compressedStream.CopyTo(cacheStream);
                    compressedStream.Position = 0;
                }
                else
                {
                    Console.WriteLine($"Reading cache file {compressedCache}");
                    compressedStream = new FileStream(compressedCache, FileMode.Open, FileAccess.Read);
                }

                using (compressedStream)
                {
                    Console.WriteLine($"Decompressing & writing {compressedStream.Length} bytes...");

                    using var zstdStream = new DecompressionStream(compressedStream);
                    using var cacheStream = new FileStream(plaintextCache, FileMode.Create, FileAccess.Write);
                    zstdStream.CopyTo(cacheStream);
                }
            }

            using var decompressedStream = new FileStream(plaintextCache, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(decompressedStream);

            Console.WriteLine($"Splitting & processing PGN file... ({decompressedStream.Length} bytes)");
            if (sPGNThread is null)
            {
                sPGNThread = new Thread(EngineThread)
                {
                    Name = "PGN processing thread"
                };

                sPGNAdded.Reset();
                sPGNThread.Start();
            }

            int skipped = await PGN.SplitAsync(reader, pgn =>
            {
                sThreadStarted.WaitOne();
                lock (sPGNQueue)
                {
                    sPGNQueue.Enqueue(new PGNData
                    {
                        PGN = pgn,
                        Engine = engine,
                        Dataset = dataset,
                        Depth = depth
                    });

                    sPGNAdded.Set();
                }
            }, true);
            Console.WriteLine($"{skipped} total games skipped due to errors!");

            sPGNAdded.Set();
            sThreadFinished.WaitOne();

            sPGNThread = null;
        }

        private static void EngineThread()
        {
            int n = 0;
            do
            {
                while (sPGNQueue.Count > 0)
                {
                    PGNData data;
                    lock (sPGNQueue)
                    {
                        data = sPGNQueue.Dequeue();
                    }

                    Console.WriteLine($"Processing game {++n}");
                    LabelPGN(data.Dataset, data.Engine, data.PGN, data.Depth);
                }

                sThreadStarted.Set();
                sPGNAdded.WaitOne();
            }
            while (sPGNQueue.Count > 0);

            sThreadFinished.Set();
            sThreadStarted.Reset();
        }

        public static void LabelPGN(Dataset dataset, UCIEngine engine, PGN pgn, int depth)
        {
            engine.NewGame();
            LabelPosition(dataset, engine, null, depth);
            
            foreach (var move in pgn.Moves)
            {
                LabelPosition(dataset, engine, move.Position, depth);
            }
        }

        public static void LabelPosition(Dataset dataset, UCIEngine engine, string? position, int depth)
        {
            string key = position ?? "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
            if (dataset.Contains(key))
            {
                return;
            }

            engine.SetPosition(position);
            var move = engine.Go(depth);
            if (move is null)
            {
                return;
            }

            var moveData = move!.Value;
            dataset.AddEntry(new PositionData
            {
                Position = key,
                PieceMoved = moveData.Move.Position.ToString(),
                BestMove = moveData.Move.Destination.ToString(),
                Promotion = moveData.Promotion
            });
        }

        public Dataset(string path)
        {
            mConnection = new SQLiteConnection(path);

            mConnection.CreateTable<PositionData>();
            mMapping = mConnection.GetMapping<PositionData>();
        }

        public void Dispose() => mConnection.Dispose();

        public void AddEntry(PositionData data) => mConnection.Insert(data);
        public bool Contains(string position) => mConnection.Find(position, mMapping) is not null;

        public int Count => throw new NotImplementedException();
        public int InputCount => throw new NotImplementedException();
        public int OutputCount => throw new NotImplementedException();

        public float[] GetInput(int index) => throw new NotImplementedException();
        public float[] GetExpectedOutput(int index) => throw new NotImplementedException();

        private readonly SQLiteConnection mConnection;
        private readonly TableMapping mMapping;
    }
}
