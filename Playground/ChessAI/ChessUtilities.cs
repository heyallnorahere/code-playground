using ChessAI.Data;
using LibChess;
using Optick.NET;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ChessAI
{
    public static class ChessUtilities
    {
        public static PieceType ParseType(char character, bool algebraic)
        {
            using var parseEvent = OptickMacros.Event();

            char lower = char.ToLower(character);
            if (!algebraic && lower == 'p')
            {
                return PieceType.Pawn;
            }

            return lower switch
            {
                'b' => PieceType.Bishop,
                'n' => PieceType.Knight,
                'r' => PieceType.Rook,
                'q' => PieceType.Queen,
                'k' => PieceType.King,
                _ => throw new ArgumentException("Invalid piece type!")
            };
        }

        public static Move ParseMove(this Engine engine, string move, out PieceType? promotion, PlayerColor? currentTurn = null)
        {
            using var parseEvent = OptickMacros.Event();

            var turn = currentTurn ?? engine.Board!.CurrentTurn;
            promotion = null;

            if (move.StartsWith("O-O"))
            {
                string castleMove = string.Empty;
                for (int i = move.Length - 1; i >= 0; i--)
                {
                    char character = move[i];
                    if (character == '+' || character == '#' || character == '?' || character == '!')
                    {
                        continue;
                    }

                    castleMove = character + castleMove;
                }

                bool queenside = false;
                if (castleMove.EndsWith("-O") && castleMove.Length == 5)
                {
                    queenside = true;
                }
                else if (castleMove.Length != 3)
                {
                    throw new ArgumentException("Invalid castle move!");
                }

                var kings = engine.FindPieces(new PieceQuery
                {
                    Color = turn,
                    Type = PieceType.King
                });

                if (kings.Count != 1)
                {
                    throw new ArgumentException("Ambiguous castle move!");
                }

                return new Move
                {
                    Position = kings[0],
                    Destination = new Coord
                    {
                        X = queenside ? 2 : Board.Width - 2,
                        Y = turn == PlayerColor.White ? 0 : Board.Width - 1
                    }
                };
            }

            if (move.Length < 2)
            {
                throw new ArgumentException("Moves must be at least 2 characters long!");
            }

            string destinationString = string.Empty;
            var pieceType = PieceType.Pawn;
            int? originFile = null;
            int? originRank = null;

            for (int i = move.Length - 1; i >= 0; i--)
            {
                char character = move[i];
                if (character == '+' || character == '#' || character == 'x' || character == '?' || character == '!')
                {
                    continue; // we dont care
                }

                if (destinationString.Length < 2)
                {
                    if (promotion is null && char.IsUpper(character))
                    {
                        var type = ParseType(character, true);
                        if (type == PieceType.King)
                        {
                            throw new ArgumentException("Cannot promote into a king!");
                        }

                        if (move[--i] != '=')
                        {
                            throw new ArgumentException("Invalid promotion notation!");
                        }

                        promotion = type;
                        continue;
                    }

                    destinationString = character + destinationString;
                    continue;
                }

                if (char.IsUpper(character))
                {
                    if (pieceType != PieceType.Pawn)
                    {
                        throw new ArgumentException("Duplicate piece type!");
                    }

                    pieceType = ParseType(character, true);
                    continue;
                }

                if (int.TryParse($"{character}", out int rank))
                {
                    if (originRank is not null)
                    {
                        throw new ArgumentException("Duplicate origin rank!");
                    }

                    if (rank < 1 || rank > Board.Width)
                    {
                        throw new ArgumentException("Invalid rank!");
                    }

                    originRank = rank - 1;
                    continue;
                }

                if (originFile is not null)
                {
                    throw new ArgumentException("Duplicate origin file!");
                }

                int file = character - 'a';
                if (file < 0 || file >= Board.Width)
                {
                    throw new ArgumentException("Invalid file!");
                }

                originFile = file;
            }

            var destination = Coord.Parse(destinationString);
            var candidates = engine.FindPieces(new PieceQuery
            {
                Color = turn,
                Type = pieceType,
                X = originFile,
                Y = originRank,
                Filter = (position, info) =>
                {
                    var legalMoves = engine.ComputeLegalMoves(position);
                    return legalMoves.Contains(destination);
                }
            });

            if (candidates.Count == 0)
            {
                throw new ArgumentException("No pieces match the description!");
            }
            else if (candidates.Count > 1)
            {
                throw new ArgumentException("Ambiguous piece selected!");
            }

            return new Move
            {
                Position = candidates[0],
                Destination = destination
            };
        }

        public static float[] GetNetworkInput(this Board board)
        {
            var input = new float[Dataset.NetworkInputCount];
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

        public static int FindBestElement<T>(this IReadOnlyList<T> data) where T : IComparable<T> => data.FindBestElement((a, b) => a.CompareTo(b));
        public static int FindBestElement<T>(this IReadOnlyList<T> data, Comparison<T> comparison)
        {
            int result = -1;
            for (int i = 0; i < data.Count; i++)
            {
                if (result >= 0 && comparison.Invoke(data[i], data[result]) <= 0)
                {
                    continue;
                }

                result = i;
            }

            return result;
        }

        public static EngineMove ParseNetworkOutput(float[] output)
        {
            if (output.Length != Dataset.NetworkOutputCount)
            {
                throw new ArgumentException("Output length mismatch!");
            }

            // if the confidence of promotion is less than 50%, we assume it just doesn't intend to promote
            const int promotionOffset = Board.Width * 4;
            int promotionIndex = output[promotionOffset..].FindBestElement();
            var promotion = promotionIndex < 0 ? PieceType.None : (PieceType)((int)PieceType.Queen + promotionIndex);

            return new EngineMove
            {
                Move = new Move
                {
                    Position = new Coord
                    {
                        X = output[..Board.Width].FindBestElement(),
                        Y = output[Board.Width..(Board.Width * 2)].FindBestElement()
                    },
                    Destination = new Coord
                    {
                        X = output[(Board.Width * 2)..(Board.Width * 3)].FindBestElement(),
                        Y = output[(Board.Width * 3)..(Board.Width * 4)].FindBestElement()
                    }
                },
                Promotion = promotion
            };
        }
    }
}