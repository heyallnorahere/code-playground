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
        public UCIEngine(string command, bool log = false)
        {
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
            mEngine.Kill();
            if (disposing)
            {
                mEngine.Dispose();
            }
        }

        private void OnResponseReceived(string data)
        {
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
            if (mLog)
            {
                Console.WriteLine($"[UCI] > {command}");
            }

            mEngine.StandardInput.WriteLine(command);
            mEngine.StandardInput.Flush();
        }

        public async Task SendCommandAsync(string command)
        {
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

        public Pond(string command, bool log, int depth)
        {
            mDepth = depth;
            mFENQueue = new Queue<string?>();

            mDisposed = false;
            mRunning = false;

            int fishCount = Environment.ProcessorCount;
            mFish = new UCIEngine[fishCount];
            mTasks = new Task<FENDigestionData>?[fishCount];

            for (int i = 0; i < fishCount; i++)
            {
                mFish[i] = new UCIEngine(command, log);
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
            mRunning = false;
            if (wait)
            {
                mAttendant?.Join();
            }
        }

        public void Feed(string? fen)
        {
            lock (mFENQueue)
            {
                mFENQueue.Enqueue(fen);
            }
        }

        private async Task<FENDigestionData> Feed(UCIEngine engine, string? fen)
        {
            await engine.SetPositionAsync(fen);
            var move = await engine.GoAsync(mDepth);

            return new FENDigestionData
            {
                Position = fen ?? DefaultFEN,
                BestMove = move
            };
        }

        private void TriggerDigestionEvent(int fishIndex)
        {
            var task = mTasks[fishIndex];
            if (task is not null && task.IsCompleted)
            {
                PositionDigested?.Invoke(task.Result);
                mTasks[fishIndex] = null;
            }
        }

        private void ThreadEntrypoint()
        {
            do
            {
                while (mFENQueue.Count > 0)
                {
                    for (int i = 0; i < mTasks.Length; i++)
                    {
                        TriggerDigestionEvent(i);
                        if (mTasks[i] is null)
                        {
                            string? fen;
                            lock (mFENQueue)
                            {
                                fen = mFENQueue.Dequeue();
                            }

                            mTasks[i] = Task.Run(async () => await Feed(mFish[i], fen));
                            break;
                        }
                    }

                    Thread.Sleep(1); // so CPU usage doesnt skyrocket
                }
            } while (mRunning);

            for (int i = 0; i < mTasks.Length; i++)
            {
                TriggerDigestionEvent(i);
            }

            mAttendant = null;
        }

        public event Action<FENDigestionData>? PositionDigested;
        public bool IsRunning => mRunning;

        private readonly Queue<string?> mFENQueue;
        private readonly UCIEngine[] mFish;
        private readonly Task<FENDigestionData>?[] mTasks;
        private Thread? mAttendant;
        private bool mRunning, mDisposed;
        private readonly int mDepth;
    }
}