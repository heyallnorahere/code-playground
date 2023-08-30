using LibChess;
using Optick.NET;
using System;
using System.Collections.Generic;

namespace ChessAI.Data
{
    public struct PGNMove
    {
        public Move Move;
        public string Position;
    }

    public struct PGN
    {
        public static PGN Parse(string text)
        {
            using var parseEvent = OptickMacros.Event();

            var lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var moveString = string.Empty;

            var result = new PGN();
            foreach (var line in lines)
            {
                int attributeBracketPosition = line.IndexOf('[');
                if (attributeBracketPosition < 0)
                {
                    if (moveString.Length > 0)
                    {
                        moveString += ' ';
                    }

                    moveString += line;
                    continue;
                }
                else if (attributeBracketPosition > 0)
                {
                    throw new ArgumentException("Invalid PGN - malformed attribute line!");
                }

                if (moveString.Length > 0)
                {
                    throw new ArgumentException("Invalid PGN - cannot set attributes after moves have been specified!");
                }

                int endBracketPosition = line.IndexOf(']');
                if (endBracketPosition != line.Length - 1)
                {
                    throw new ArgumentException("Invalid PGN - malformed attribute line!");
                }

                string attributeData = line[(attributeBracketPosition + 1)..endBracketPosition]; // i could just substring 1..^1 but this is clearer
                var splitTerms = attributeData.Split(' ');

                var terms = new List<string>();
                string? currentTerm = null;

                foreach (var splitTerm in splitTerms)
                {
                    var newTerm = splitTerm.Replace("\"", null);
                    int quoteCount = splitTerm.Length - newTerm.Length;

                    if (quoteCount % 2 != 1)
                    {
                        if (currentTerm is not null)
                        {
                            currentTerm += ' ' + newTerm;
                        }
                        else
                        {
                            terms.Add(newTerm);
                        }
                    }
                    else
                    {
                        if (currentTerm is null)
                        {
                            currentTerm = newTerm;
                        }
                        else
                        {
                            terms.Add($"{currentTerm} {newTerm}");
                            currentTerm = null;
                        }
                    }
                }

                if (terms.Count != 2)
                {
                    throw new ArgumentException("Invalid PGN - attributes must have 2 terms!");
                }

                result.Attributes[terms[0]] = terms[1];
            }

            ParseMoves(moveString, result.Moves);
            return result;
        }

        public static void ParseMoves(string moveString, IList<PGNMove> moves)
        {
            using var parseEvent = OptickMacros.Event();
            moves.Clear();

            using var board = Board.Create(); // creates a board with the default position
            using var engine = new Engine
            {
                Board = board
            };

            int currentTurn = 0;
            bool shouldAdvanceTurn = true;

            var terms = moveString.Split(' ');
            for (int i = 0; i < terms.Length; i++)
            {
                var term = terms[i];
                if (i < terms.Length - 1)
                {
                    if (shouldAdvanceTurn)
                    {
                        if (!int.TryParse(term[..^1], out int newTurn) || term[^1] != '.')
                        {
                            throw new ArgumentException("Invalid turn term!");
                        }

                        if (newTurn - currentTurn != 1)
                        {
                            throw new ArgumentException("Turn increment must be exactly 1!");
                        }

                        currentTurn = newTurn;
                        shouldAdvanceTurn = false;
                    }
                    else
                    {
                        var move = engine.ParseMove(term, out PieceType? promotion);
                        if (!engine.CommitMove(move))
                        {
                            throw new ArgumentException($"Illegal move: {term}");
                        }

                        if (promotion is not null && !engine.Promote(promotion.Value))
                        {
                            throw new ArgumentException("Invalid promotion!");
                        }

                        moves.Add(new PGNMove
                        {
                            Move = move,
                            Position = board.SerializeFEN()
                        });

                        if (board.CurrentTurn == PlayerColor.White)
                        {
                            shouldAdvanceTurn = true;
                        }
                    }
                }
                else if (term.Length % 2 != 1 || term[(term.Length - 1) / 2] != '-')
                {
                    throw new ArgumentException("Invalid final term!");
                }
            }
        }

        public PGN()
        {
            Attributes = new Dictionary<string, string>();
            Moves = new List<PGNMove>();
        }

        public Dictionary<string, string> Attributes;
        public List<PGNMove> Moves;
    }
}