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

    public sealed class Dataset : IDataset, IDisposable
    {
        public const int NetworkInputCount = Board.Width * Board.Width + 3; // data in a FEN string
        public const int NetworkOutputCount = Board.Width * 4 + 4; // rank and file, source and destination, plus promotion piece types

        private static readonly HashSet<string?> sSubmittedFENs;
        static Dataset()
        {
            sSubmittedFENs = new HashSet<string?>();
        }

        public static async Task PullAndLabelAsync(Dataset dataset, string command, bool logUCI, int year, int month, int depth)
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

            using var pond = new Pond(command, logUCI, depth);
            pond.PositionDigested += digestion =>
            {
                sSubmittedFENs.Remove(digestion.Position);
                if (digestion.BestMove is null)
                {
                    return;
                }

                var moveData = digestion.BestMove.Value;
                dataset.AddEntry(new PositionData
                {
                    ID = dataset.mCount,
                    Position = digestion.Position,
                    PieceMoved = moveData.Move.Position.ToString(),
                    BestMove = moveData.Move.Destination.ToString(),
                    Promotion = moveData.Promotion
                });
            };

            pond.Start();

            Console.WriteLine($"Splitting & processing PGN file... ({decompressedStream.Length} bytes)");
            int skipped = await PGN.SplitAsync(reader, pgn => LabelPGN(dataset, pond, pgn), true);

            pond.Stop(true);
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
            if (dataset.Contains(fen ?? Pond.DefaultFEN) || !sSubmittedFENs.Add(fen))
            {
                return;
            }

            pond.Feed(fen);
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
