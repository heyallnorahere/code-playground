using LibChess;
using System;
using System.Linq;

namespace ChessAI
{
    public static class ChessUtilities
    {
        public static PieceType ParseType(char character, bool algebraic)
        {
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
            var turn = currentTurn ?? engine.Board!.CurrentTurn;
            promotion = null;

            if (move.StartsWith("O-O"))
            {
                bool queenside = false;
                if (move.EndsWith("-O") && move.Length == 5)
                {
                    queenside = true;
                }
                else if (move.Length != 3)
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
                        X = queenside ? 1 : Board.Width - 2,
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
    }
}