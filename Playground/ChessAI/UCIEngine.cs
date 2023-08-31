using LibChess;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ChessAI
{
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
                    Arguments = $"{(isWindows ? "/c" : "-c")} \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true
                }
            };

            if (!mEngine.Start())
            {
                throw new InvalidOperationException("Failed to start engine!");
            }

            mEngine.OutputDataReceived += (s, e) =>
            {
                var data = e.Data;
                if (data is null)
                {
                    return;
                }

                OnResponseReceived(data);
                ResponseReceived?.Invoke(data);
            };
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
            if (!mInitialized)
            {
                SendCommand("uci");
                WaitUntilReady();

                return;
            }

            Console.WriteLine($"[UCI] {data}");

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

                string bestMoveString = data[bestMovePrefix.Length..delimiterPosition];
                if (bestMoveString.Length != 4)
                {
                    throw new ArgumentException("Moves should be exactly 4 characters long!");
                }

                mBestMove = new Move
                {
                    Position = Coord.Parse(data[..2]),
                    Destination = Coord.Parse(data[2..])
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

        public Move Go(int depth)
        {
            mUCIReady.WaitOne();
            WaitUntilReady();

            var depthSpec = depth < 0 ? "infinite" : $"depth {depth}";
            SendCommand($"go {depthSpec}");

            mMoveReturned.WaitOne();
            return mBestMove!.Value;
        }

        public async Task<Move> GoAsync(int depth)
        {
            await Task.Run(mUCIReady.WaitOne);
            await WaitUntilReadyAsync();

            var depthSpec = depth < 0 ? "infinite" : $"depth {depth}";
            await SendCommandAsync($"go {depthSpec}");

            await Task.Run(mMoveReturned.WaitOne);
            return mBestMove!.Value;
        }

        public void SendCommand(string command) => mEngine.StandardInput.WriteLine(command);
        public async Task SendCommandAsync(string command) => await mEngine.StandardInput.WriteLineAsync(command);

        public event Action<string>? ResponseReceived;

        public IReadOnlyDictionary<string, string> ID => mID;

        private readonly ManualResetEvent mUCIReady;
        private readonly AutoResetEvent mMoveReturned, mReadyCheck;

        private readonly Dictionary<string, string?> mOptions;
        private readonly Dictionary<string, string> mID;

        private Move? mBestMove;
        private readonly Process mEngine;
        private bool mDisposed, mInitialized;
    }
}