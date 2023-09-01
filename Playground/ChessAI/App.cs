using ChessAI.Data;
using CodePlayground;
using CodePlayground.Graphics;
using MachineLearning;
using System;
using System.IO;
using System.Text;

namespace ChessAI
{
    internal enum CommandLineCommand
    {
        None,
        LabelData,
        Train,
        GUI
    }

    [ApplicationTitle("Chess AI")]
    internal sealed class App : GraphicsApplication
    {
        private static void PrintUsage()
        {
            const string usage = """
                Basic Usage:
                    CodePlayground.dll ChessAI.dll <command> [<argument>...]

                Commands:
                    label <year> <month> [depth] [command] [log uci]
                        Pulls a month of games from the Lichess games database (https://database.lichess.org/), labels the dataset with a UCI chess engine, and serializes it.

                    train <minimum> [batch size] [learning rate]
                        Trains the neural network until the average absolute cost of a batch reaches a minimum value, or until the program receives an interrupt command.

                    gui
                        Opens a GUI that allows one to play against the network.

                    help
                        Displays this help message.

                Arguments:
                    year
                        In combination with the month, the year from which to pull from the Lichess database. If less than 100, will be years after 2000.

                    month
                        In combination with the year, the month from which to pull from the Lichess database. Can be a string or an integer.

                    depth
                        The depth at which to search while labeling. Default is infinite.

                    command
                        The command with which to start up the UCI engine. Default is "stockfish"

                    log uci
                        Enable logging of UCI communication between the program and the UCI engine. Default is false.

                    minimum
                        The minimum average absolute cost of a batch. When this value is reached, training will cease.

                    batch size
                        The size of one batch for the network to train on. Each batch will be a grouping out of which to average deltas. Default is 100.

                    learning rate
                        The rate at which this network should learn. Merely a factor at which to scale the deltas. Default is 0.1.
                """;

            Console.WriteLine(usage);
        }

        public App()
        {
            mHeadless = false;
            mCommand = CommandLineCommand.None;

            mDepth = -1;
            mEngine = "stockfish";
            mLogUCI = false;

            mCurrentBatch = 0;
            mBatchSize = 100;
            mLearningRate = 0.1f;

            Load += OnLoad;
            InputReady += OnInputReady;
            Closing += OnClose;

            Update += OnUpdate;
            Render += OnRender;
        }

        private void ParseLabelArguments(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("Year and month not provided");
            }

            mYear = int.Parse(args[0]);
            if (mYear < 100)
            {
                mYear += 2000;
            }

            if (mYear < 2013)
            {
                throw new ArgumentException("No games before January 2013 are recorded! (https://database.lichess.org/)");
            }

            string monthString = args[1];
            if (!int.TryParse(monthString, out mMonth))
            {
                mMonth = monthString[..3].ToLower() switch // substring to enable abbreviation
                {
                    "jan" => 1,
                    "feb" => 2,
                    "mar" => 3,
                    "apr" => 4,
                    "may" => 5,
                    "jun" => 6,
                    "jul" => 7,
                    "aug" => 8,
                    "sep" => 9,
                    "oct" => 10,
                    "nov" => 11,
                    "dec" => 12,
                    _ => throw new ArgumentException($"Invalid month: {monthString}")
                };
            }

            var currentTime = DateTime.Now;
            if (mYear > currentTime.Year || (mYear == currentTime.Year && mMonth == currentTime.Month))
            {
                throw new ArgumentException("Games from the specified timeframe have not been published!");
            }

            if (args.Length > 2)
            {
                var depthArgument = args[2];
                if (depthArgument != "infinite")
                {
                    mDepth = int.Parse(depthArgument);
                }

                if (args.Length > 3)
                {
                    mEngine = args[3];
                    if (args.Length > 4)
                    {
                        mLogUCI = bool.Parse(args[4]);
                    }
                }
            }
        }

        private void ParseTrainingArguments(string[] args)
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("Minimum not provided");
            }

            mMinimumAbsoluteAverageCost = float.Parse(args[0]);
            if (args.Length > 1)
            {
                mBatchSize = int.Parse(args[1]);
                if (args.Length > 2)
                {
                    mLearningRate = float.Parse(args[2]);
                }
            }
        }

        protected override void ParseArguments()
        {
            try
            {
                var args = CommandLineArguments;
                if (args.Length > 0)
                {
                    string command = args[0];
                    if (command != "help")
                    {
                        var commandArguments = args[1..];
                        switch (command)
                        {
                            case "label":
                                mCommand = CommandLineCommand.LabelData;
                                mHeadless = true;

                                ParseLabelArguments(commandArguments);
                                break;
                            case "train":
                                mCommand = CommandLineCommand.Train;
                                mHeadless = true;

                                ParseTrainingArguments(commandArguments);
                                break;
                            case "gui":
                                mCommand = CommandLineCommand.GUI;
                                throw new NotImplementedException();
                            default:
                                throw new ArgumentException($"Unknown command: {command}");
                        }

                        return;
                    }
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine(exc.Message);
            }

            PrintUsage();
            mHeadless = true;
        }

        private const string networkPath = "network.json";
        private void LoadNetwork(Encoding? encoding = null)
        {
            if (File.Exists(networkPath))
            {
                using var stream = new FileStream(networkPath, FileMode.Open, FileAccess.Read);
                mNetwork = Network.Load(stream, encoding);
            }
            else
            {
                int hiddenLayerSize = (int)float.Round((Dataset.NetworkInputCount + Dataset.NetworkOutputCount) / 2f);
                mNetwork = new Network(new int[]
                {
                    Dataset.NetworkInputCount,
                    hiddenLayerSize,
                    Dataset.NetworkOutputCount
                });
            }
        }

        private void SerializeNetwork(Encoding? encoding = null)
        {
            if (mNetwork is null)
            {
                return;
            }

            using var stream = new FileStream(networkPath, FileMode.Create, FileAccess.Write);
            Network.Save(mNetwork, stream, encoding);
        }

        private void OnLoad()
        {
            if (mCommand == CommandLineCommand.None)
            {
                return;
            }

            if (mCommand != CommandLineCommand.LabelData)
            {
                CreateGraphicsContext();

                var encoding = Encoding.UTF8;
                LoadNetwork(encoding);
                Console.CancelKeyPress += (s, e) => SerializeNetwork(encoding);
            }

            InitializeOptick();
            InitializeImGui();

            switch (mCommand)
            {
                case CommandLineCommand.Train:
                    mTrainer = new Trainer(GraphicsContext!, mBatchSize, mLearningRate);
                    mTrainer.OnBatchResults += results =>
                    {
                        Console.WriteLine($"Batch {++mCurrentBatch} results:");
                        Console.WriteLine($"\tAverage absolute cost: {results.AverageAbsoluteCost}");
                        // todo: vulkan query pool

                        if (results.AverageAbsoluteCost < mMinimumAbsoluteAverageCost)
                        {
                            mTrainer.Stop();
                        }
                    };

                    break;
                case CommandLineCommand.GUI:
                    throw new NotImplementedException();
            }
        }

        private void OnInputReady() => InitializeImGui();
        private void InitializeImGui()
        {
            // todo: initialize imgui
        }

        private void OnClose()
        {
            var context = GraphicsContext;
            context?.Device?.ClearQueues();

            mTrainer?.Dispose();
            SerializeNetwork(Encoding.UTF8);

            context?.Dispose();
        }

        private void OnUpdate(double delta)
        {
            if (mCommand != CommandLineCommand.GUI)
            {
                return;
            }

            throw new NotImplementedException();
        }

        private void OnRender(FrameRenderInfo renderInfo)
        {
            const string datasetPath = "dataset.sqlite";
            switch (mCommand)
            {
                case CommandLineCommand.LabelData:
                    using (var engine = new UCIEngine(mEngine, mLogUCI))
                    {
                        using var dataset = new Dataset(datasetPath);
                        Console.CancelKeyPress += (s, e) =>
                        {
                            lock (dataset)
                            {
                                Console.WriteLine("Closing database...");
                                dataset.Dispose();
                            }
                        };

                        Dataset.PullAndLabelAsync(dataset, engine, mYear, mMonth, mDepth).Wait();
                    }

                    break;
                case CommandLineCommand.Train:
                    using (var dataset = new Dataset(datasetPath))
                    {
                        Console.WriteLine($"Dataset of {dataset.Count} positions loaded");

                        mTrainer!.Start(dataset, mNetwork!);
                        while (mTrainer.Running)
                        {
                            mTrainer.Update(true);
                        }
                    }

                    break;
                case CommandLineCommand.GUI:
                    throw new NotImplementedException();
            }
        }

        public override bool ShouldRunHeadless => mHeadless;

        private bool mHeadless;
        private CommandLineCommand mCommand;
        private Network? mNetwork;

        private int mYear, mMonth;
        private int mDepth;
        private string mEngine;
        private bool mLogUCI;

        private float mMinimumAbsoluteAverageCost, mLearningRate;
        private int mBatchSize;
        private Trainer? mTrainer;
        private int mCurrentBatch;
    }
}