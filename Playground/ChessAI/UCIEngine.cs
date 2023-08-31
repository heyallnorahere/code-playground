using LibChess;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ChessAI
{
    public struct EngineMove
    {
        public Move Move;
        public PieceType Promotion;
    }

    public sealed class UCIEngine : IDisposable
    {
        public UCIEngine(string command)
        {
            mDisposed = false;
            mInitialized = false;

            mOptions = new Dictionary<string, string?>();
            mID = new Dictionary<string, string>();

            mUCIReady = new ManualResetEvent(false);
            mMoveReturned = new AutoResetEvent(false);
            mReadyCheck = new AutoResetEvent(false);

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            mEngine = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? "cmd.exe" : "/bin/bash",
                    Arguments = $"{(isWindows ? "/c" : "-c")} {command}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true
                }
            };

            mEngine.OutputDataReceived += (s, e) =>
            {
                var data = e.Data;
                if (string.IsNullOrEmpty(data))
                {
                    return;
                }

                OnResponseReceived(data);
                ResponseReceived?.Invoke(data);
            };

            if (!mEngine.Start())
            {
                throw new InvalidOperationException("Failed to start engine!");
            }

            mEngine.BeginOutputReadLine();
        }

        ~UCIEngine()
        {
            if (!mDisposed)
            {
                Dispose(false);
                mDisposed = true;
            }
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Dispose(true);
            mDisposed = true;
        }

        private void Dispose(bool disposing)
        {
            mEngine.Kill();
            if (disposing)
            {
                mEngine.Dispose();
            }
        }

        private void OnResponseReceived(string data)
        {
            Console.WriteLine($"[UCI] {data}");
            if (!mInitialized)
            {
                mInitialized = true;

                SendCommand("uci");
                return;
            }

            if (data == "uciok")
            {
                mUCIReady.Set();
                return;
            }

            if (data == "readyok")
            {
                mReadyCheck.Set();
                return;
            }

            const string bestMovePrefix = "bestmove ";
            if (data.StartsWith(bestMovePrefix))
            {
                int delimiterPosition = data.IndexOf(' ', bestMovePrefix.Length);
                if (delimiterPosition < 0)
                {
                    delimiterPosition = data.Length;
                }

                string bestMoveString = data[bestMovePrefix.Length..delimiterPosition];
                if (bestMoveString == "(none)")
                {
                    mBestMove = null;
                    mMoveReturned.Set();

                    return;
                }

                if (bestMoveString.Length < 4)
                {
                    throw new ArgumentException("Moves should be at least 4 characters long!");
                }

                mBestMove = new EngineMove
                {
                    Move = new Move
                    {
                        Position = Coord.Parse(bestMoveString[..2]),
                        Destination = Coord.Parse(bestMoveString[2..4])
                    },
                    Promotion = bestMoveString.Length > 4 ? ChessUtilities.ParseType(bestMoveString[4], true) : PieceType.None
                };

                mMoveReturned.Set();
            }

            const string idPrefix = "id ";
            if (data.StartsWith(idPrefix))
            {
                int delimiterPosition = data.IndexOf(' ', idPrefix.Length);
                if (delimiterPosition < 0)
                {
                    return;
                }

                string idName = data[idPrefix.Length..delimiterPosition];
                string idValue = data[(delimiterPosition + 1)..];

                mID[idName] = idValue;
            }

            const string optionPrefix = "option name ";
            if (data.StartsWith(optionPrefix))
            {
                int typePosition = data.IndexOf(" type");
                if (typePosition < 0)
                {
                    return;
                }

                var optionName = data[optionPrefix.Length..typePosition];
                string? optionValue = null;

                const string defaultToken = "default";
                int defaultPosition = data.IndexOf(defaultToken);
                if (defaultPosition >= 0)
                {
                    if (defaultPosition < typePosition)
                    {
                        return;
                    }

                    int defaultValueStartPosition = defaultPosition + defaultToken.Length;
                    if (defaultValueStartPosition < data.Length)
                    {
                        optionValue = data[(defaultValueStartPosition + 1)..];
                    }
                    else
                    {
                        optionValue = string.Empty;
                    }
                }

                mOptions[optionName] = optionValue;
                return;
            }
        }

        public string? GetOption(string optionName)
        {
            mOptions.TryGetValue(optionName, out var optionValue);
            return optionValue;
        }

        public void WaitUntilReady()
        {
            mUCIReady.WaitOne();

            SendCommand("isready");
            mReadyCheck.WaitOne();
        }

        public async Task WaitUntilReadyAsync()
        {
            await Task.Run(mUCIReady.WaitOne);

            await SendCommandAsync("isready");
            await Task.Run(mReadyCheck.WaitOne);
        }

        public void NewGame()
        {
            mUCIReady.WaitOne();
            WaitUntilReady();

            SendCommand("ucinewgame");
        }

        public async Task NewGameAsync()
        {
            await Task.Run(mUCIReady.WaitOne);
            await WaitUntilReadyAsync();

            await SendCommandAsync("ucinewgame");
        }

        public void SetPosition(string? position)
        {
            mUCIReady.WaitOne();
            WaitUntilReady();

            var sentPosition = position is null ? "startpos" : $"fen {position}";
            SendCommand($"position {sentPosition}");
        }

        public async Task SetPositionAsync(string? position)
        {
            await Task.Run(mUCIReady.WaitOne);
            await WaitUntilReadyAsync();

            var sentPosition = position is null ? "startpos" : $"fen {position}";
            await SendCommandAsync($"position {sentPosition}");
        }

        public EngineMove? Go(int depth)
        {
            mUCIReady.WaitOne();
            WaitUntilReady();

            var depthSpec = depth < 0 ? "infinite" : $"depth {depth}";
            SendCommand($"go {depthSpec}");

            mMoveReturned.WaitOne();
            return mBestMove;
        }

        public async Task<EngineMove?> GoAsync(int depth)
        {
            await Task.Run(mUCIReady.WaitOne);
            await WaitUntilReadyAsync();

            var depthSpec = depth < 0 ? "infinite" : $"depth {depth}";
            await SendCommandAsync($"go {depthSpec}");

            await Task.Run(mMoveReturned.WaitOne);
            return mBestMove;
        }

        public void SendCommand(string command)
        {
            Console.WriteLine($"[UCI] > {command}");

            mEngine.StandardInput.WriteLine(command);
            mEngine.StandardInput.Flush();
        }

        public async Task SendCommandAsync(string command)
        {
            Console.WriteLine($"[UCI] > {command}");

            await mEngine.StandardInput.WriteLineAsync(command);
            await mEngine.StandardInput.FlushAsync();
        }

        public event Action<string>? ResponseReceived;

        public IReadOnlyDictionary<string, string> ID => mID;

        private readonly ManualResetEvent mUCIReady;
        private readonly AutoResetEvent mMoveReturned, mReadyCheck;

        private readonly Dictionary<string, string?> mOptions;
        private readonly Dictionary<string, string> mID;

        private EngineMove? mBestMove;
        private readonly Process mEngine;
        private bool mDisposed, mInitialized;
    }
}