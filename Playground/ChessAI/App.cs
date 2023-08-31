using ChessAI.Data;
using CodePlayground;
using CodePlayground.Graphics;
using System;

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
                    label <year> <month>
                        Pulls a month of games from the Lichess games database (https://database.lichess.org/), labels the dataset with Stockfish, and serializes it.

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

                    minimum
                        The minimum average absolute cost of a batch. When this value is reached, training will cease.

                    batch size
                        The size of one batch for the network to train on. Each batch will be a grouping out of which to average deltas.

                    learning rate
                        The rate at which this network should learn. Merely a factor at which to scale the deltas.
                """;

            Console.WriteLine(usage);
        }

        public App()
        {
            mHeadless = false;
            mCommand = CommandLineCommand.None;

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
                        switch (command)
                        {
                            case "label":
                                mCommand = CommandLineCommand.LabelData;
                                mHeadless = true;

                                ParseLabelArguments(args[1..]);
                                break;
                            case "train":
                                mCommand = CommandLineCommand.Train;
                                mHeadless = true;

                                throw new NotImplementedException();
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

        private void OnLoad()
        {
            if (mCommand == CommandLineCommand.None)
            {
                return;
            }

            if (mCommand != CommandLineCommand.LabelData)
            {
                CreateGraphicsContext();
            }

            InitializeOptick();
            InitializeImGui();

            switch (mCommand)
            {
                case CommandLineCommand.Train:
                    throw new NotImplementedException();
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

            // todo: clean up

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
            switch (mCommand)
            {
                case CommandLineCommand.LabelData:
                    // todo: label
                    break;
                case CommandLineCommand.Train:
                    throw new NotImplementedException();
                case CommandLineCommand.GUI:
                    throw new NotImplementedException();
            }
        }

        public override bool ShouldRunHeadless => mHeadless;

        private bool mHeadless;
        private CommandLineCommand mCommand;
        private int mYear, mMonth;
    }
}