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

        [PrimaryKey, Indexed]
        public int ID { get; set; }

        [Unique]
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
        public const int NetworkInputCount = Board.Width * Board.Width + 3; // data in a FEN string
        public const int NetworkOutputCount = Board.Width * 4 + 4; // rank and file, source and destination, plus promotion piece types

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
                ID = dataset.mCount,
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
            mCount = mConnection.Table<PositionData>().Count();
        }

        public void Dispose() => mConnection.Dispose();

        public void AddEntry(PositionData data)
        {
            mConnection.Insert(data);
            mCount++;
        }

        public bool Contains(string position) => mConnection.Find<PositionData>(data => data.Position == position) is not null;

        public int Count => mCount;
        public int InputCount => NetworkInputCount;
        public int OutputCount => NetworkOutputCount;

        public float[] GetInput(int index)
        {
            var data = mConnection.Table<PositionData>().ElementAt(index);
            using var board = Board.Create(data.Position);

            if (board is null)
            {
                throw new InvalidOperationException("Failed to interpret FEN!");
            }

            var input = new float[NetworkInputCount];
            for (int y = 0; y < Board.Width; y++)
            {
                int rankOffset = y * Board.Width;
                for (int x = 0; x < Board.Width; x++)
                {
                    bool pieceExists = board.GetPiece((x, y), out PieceInfo piece);

                    int pieceId = (int)piece.Type;
                    if (pieceExists && piece.Color == PlayerColor.Black)
                    {
                        pieceId += Enum.GetValues<PieceType>().Length - 1;
                    }

                    int fileOffset = rankOffset + x;
                    input[fileOffset] = pieceId;
                }
            }

            int dataOffset = Board.Width * Board.Width;
            var currentTurn = board.CurrentTurn;
            var enPassantTarget = board.EnPassantTarget;

            var whiteCastling = board.GetCastlingAvailability(PlayerColor.White);
            var blackCastling = board.GetCastlingAvailability(PlayerColor.Black);

            input[dataOffset] = (int)currentTurn;
            input[dataOffset + 1] = (int)whiteCastling | ((int)blackCastling << 2);
            input[dataOffset + 2] = enPassantTarget is null ? -1 : (enPassantTarget.Value.Y * Board.Width + enPassantTarget.Value.X);

            return input;
        }

        public float[] GetExpectedOutput(int index)
        {
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
