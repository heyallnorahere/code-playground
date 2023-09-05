using LibChess;
using Optick.NET;
using Silk.NET.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ChessAI.Data
{
    public struct PGNMove
    {
        public Move Move;
        public string Position;
        public float[] NetworkInput;
    }

    public struct PGN
    {
        private static bool Parse(string text, Action<PGN> callback, bool skip)
        {
            using var parseEvent = OptickMacros.Event();
            if (skip)
            {
                try
                {
                    var pgn = Parse(text);
                    callback.Invoke(pgn);
                }
                catch (Exception)
                {
                    return true;
                }
            }
            else
            {
                var pgn = Parse(text);
                callback.Invoke(pgn);
            }

            return false;
        }

        public static async Task<int> SplitAsync(TextReader reader, Action<PGN> callback, bool skipOnError)
        {
            using var splitEvent = OptickMacros.Event();
            int skippedGames = 0;

            var builder = new StringBuilder();
            bool attributesEnded = false;

            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith('['))
                {
                    if (attributesEnded)
                    {
                        string pgnText = builder.ToString();
                        if (Parse(pgnText, callback, skipOnError))
                        {
                            skippedGames++;
                        }

                        builder.Clear();
                        attributesEnded = false;
                    }
                }
                else if (!attributesEnded)
                {
                    attributesEnded = true;
                }

                builder.Append(line);
            }

            if (builder.Length > 0 && Parse(builder.ToString(), callback, skipOnError))
            {
                skippedGames++;
            }

            return skippedGames;
        }

        public static PGN Parse(string text)
        {
            using var parseEvent = OptickMacros.Event();

            var lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var moveString = string.Empty;

            var result = new PGN();
            foreach (var line in lines)
            {
                var remainingLine = line;
                while (remainingLine is not null)
                {
                    int attributeBracketPosition = remainingLine.IndexOf('[');
                    if (attributeBracketPosition != 0 || moveString.Length > 0)
                    {
                        if (moveString.Length > 0)
                        {
                            moveString += ' ';
                        }

                        moveString += remainingLine;
                        break;
                    }

                    if (moveString.Length > 0)
                    {
                        throw new ArgumentException("Invalid PGN - cannot set attributes after moves have been specified!");
                    }

                    int endBracketPosition = remainingLine.IndexOf(']');
                    if (endBracketPosition < 0)
                    {
                        throw new ArgumentException("Invalid PGN - malformed attribute line!");
                    }

                    string attributeData = remainingLine[(attributeBracketPosition + 1)..endBracketPosition]; // i could just substring 1..^1 but this is clearer
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
                    if (endBracketPosition != remainingLine.Length - 1)
                    {
                        var remaining = remainingLine[(endBracketPosition + 1)..];
                        remainingLine = remaining.Length > 0 ? remaining : null;
                    }
                }
            }

            ParseMoves(moveString, result.Moves);
            return result;
        }

        public static void ParseMoves(string moveString, IList<PGNMove> moves)
        {
            using var parseMovesEvent = OptickMacros.Event();

            using var parseEvent = OptickMacros.Event();
            moves.Clear();

            using var board = Board.Create(); // creates a board with the default position
            using var engine = new Engine
            {
                Board = board
            };

            int currentTurn = 0;
            bool shouldAdvanceTurn = true;
            bool ignore = false;

            var terms = moveString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < terms.Length; i++)
            {
                var term = terms[i];
                if (term.StartsWith('(') || term.StartsWith('{'))
                {
                    ignore = true;
                    continue;
                }

                if (term.EndsWith(')') || term.EndsWith('}'))
                {
                    ignore = false;
                    continue;
                }

                if (term.StartsWith(';') || ignore)
                {
                    continue;
                }

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
                        if (term.Contains("..."))
                        {
                            continue;
                        }

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
                            Position = board.SerializeFEN(),
                            NetworkInput = board.GetNetworkInput()
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