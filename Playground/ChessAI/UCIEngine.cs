using LibChess;
using Optick.NET;
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
        public UCIEngine(string command, bool log = false)
        {
            using var constructorEvent = OptickMacros.Event();

            mDisposed = false;
            mInitialized = false;
            mLog = log;

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
            using var disposeEvent = OptickMacros.Event();

            mEngine.Kill();
            if (disposing)
            {
                mEngine.Dispose();
            }
        }

        private void OnResponseReceived(string data)
        {
            using var responseEvent = OptickMacros.Event();
            if (mLog)
            {
                Console.WriteLine($"[UCI] {data}");
            }

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
            using var getOptionEvent = OptickMacros.Event();

            mOptions.TryGetValue(optionName, out var optionValue);
            return optionValue;
        }

        public void WaitUntilReady()
        {
            using var waitEvent = OptickMacros.Event();
            mUCIReady.WaitOne();

            SendCommand("isready");
            mReadyCheck.WaitOne();
        }

        public async Task WaitUntilReadyAsync()
        {
            using var waitEvent = OptickMacros.Event();
            await Task.Run(mUCIReady.WaitOne);

            await SendCommandAsync("isready");
            await Task.Run(mReadyCheck.WaitOne);
        }

        public void NewGame()
        {
            using var newGameEvent = OptickMacros.Event();

            mUCIReady.WaitOne();
            WaitUntilReady();

            SendCommand("ucinewgame");
        }

        public async Task NewGameAsync()
        {
            using var newGameEvent = OptickMacros.Event();

            await Task.Run(mUCIReady.WaitOne);
            await WaitUntilReadyAsync();

            await SendCommandAsync("ucinewgame");
        }

        public void SetPosition(string? position)
        {
            using var setPositionEvent = OptickMacros.Event();

            mUCIReady.WaitOne();
            WaitUntilReady();

            var sentPosition = position is null ? "startpos" : $"fen {position}";
            SendCommand($"position {sentPosition}");
        }

        public async Task SetPositionAsync(string? position)
        {
            using var setPositionEvent = OptickMacros.Event();

            await Task.Run(mUCIReady.WaitOne);
            await WaitUntilReadyAsync();

            var sentPosition = position is null ? "startpos" : $"fen {position}";
            await SendCommandAsync($"position {sentPosition}");
        }

        public EngineMove? Go(int depth)
        {
            using var goEvent = OptickMacros.Event();

            mUCIReady.WaitOne();
            WaitUntilReady();

            var depthSpec = depth < 0 ? "infinite" : $"depth {depth}";
            SendCommand($"go {depthSpec}");

            mMoveReturned.WaitOne();
            return mBestMove;
        }

        public async Task<EngineMove?> GoAsync(int depth)
        {
            using var goEvent = OptickMacros.Event();

            await Task.Run(mUCIReady.WaitOne);
            await WaitUntilReadyAsync();

            var depthSpec = depth < 0 ? "infinite" : $"depth {depth}";
            await SendCommandAsync($"go {depthSpec}");

            await Task.Run(mMoveReturned.WaitOne);
            return mBestMove;
        }

        public void SendCommand(string command)
        {
            using var sendCommandEvent = OptickMacros.Event();

            if (mLog)
            {
                Console.WriteLine($"[UCI] > {command}");
            }

            mEngine.StandardInput.WriteLine(command);
            mEngine.StandardInput.Flush();
        }

        public async Task SendCommandAsync(string command)
        {
            using var sendCommandEvent = OptickMacros.Event();

            if (mLog)
            {
                Console.WriteLine($"[UCI] > {command}");
            }

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
        private readonly bool mLog;
        private bool mDisposed, mInitialized;
    }

    public struct FENDigestionData
    {
        public string Position;
        public EngineMove? BestMove;
    }

    // fishe
    public sealed class Pond : IDisposable
    {
        public const string DefaultFEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        public Pond(string command, int depth)
        {
            using var constructorEvent = OptickMacros.Event();

            mDepth = depth;
            mFENQueue = new Queue<string?>();

            mDisposed = false;
            mRunning = false;

            int fishCount = Environment.ProcessorCount;
            mFish = new UCIEngine[fishCount];

            for (int i = 0; i < fishCount; i++)
            {
                mFish[i] = new UCIEngine(command, false);
            }
        }

        ~Pond()
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
            using var disposeEvent = OptickMacros.Event();
            Stop(true);

            if (disposing)
            {
                foreach (var fish in mFish)
                {
                    fish.Dispose();
                }
            }
        }

        public void Start()
        {
            using var startEvent = OptickMacros.Event();
            if (mRunning)
            {
                return;
            }

            mRunning = true;
            if (mAttendant is null)
            {
                mAttendant = new Thread(ThreadEntrypoint)
                {
                    Name = "Attendant"
                };

                mAttendant.Start();
            }
        }

        public void Stop(bool wait)
        {
            using var stopEvent = OptickMacros.Event();

            mRunning = false;
            if (wait)
            {
                mAttendant?.Join();
            }
        }

        public void Feed(string? fen)
        {
            using var feedEvent = OptickMacros.Event();
            lock (mFENQueue)
            {
                mFENQueue.Enqueue(fen);
            }
        }

        private FENDigestionData Feed(UCIEngine engine, string? fen)
        {
            using var feedEvent = OptickMacros.Event();

            engine.SetPosition(fen);
            var move = engine.Go(mDepth);

            return new FENDigestionData
            {
                Position = fen ?? DefaultFEN,
                BestMove = move
            };
        }

        private void ThreadEntrypoint()
        {
            Parallel.For(0, mFish.Length, i =>
            {
                int fishNumber = i + 1;

                using var threadScope = new ThreadScope($"Pond attendant arm #{fishNumber}");
                using var armFeedingEvent = OptickMacros.Event("Arm feeding");
                OptickMacros.Tag("Fish number", fishNumber);

                do
                {
                    while (mFENQueue.Count > 0)
                    {
                        string? fen;
                        lock (mFENQueue)
                        {
                            if (!mFENQueue.TryDequeue(out fen))
                            {
                                Thread.Sleep(1); // so CPU usage doesnt skyrocket
                                continue;
                            }
                        }

                        using var feedFishEvent = OptickMacros.Event("Feed fish");
                        OptickMacros.Tag("Fish number", fishNumber);

                        var data = Feed(mFish[i], fen);
                        PositionDigested?.Invoke(data);
                    }

                    Thread.Sleep(1);
                } while (mRunning);
            });

            mAttendant = null;
        }

        public event Action<FENDigestionData>? PositionDigested;
        public bool IsRunning => mRunning;

        private readonly Queue<string?> mFENQueue;
        private readonly UCIEngine[] mFish;
        private Thread? mAttendant;
        private bool mRunning, mDisposed;
        private readonly int mDepth;
    }
}