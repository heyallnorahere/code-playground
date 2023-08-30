using ChessAI.Data;
using CodePlayground;
using CodePlayground.Graphics;
using System;
using System.IO;
using System.Text;

namespace ChessAI
{
    [ApplicationTitle("Chess AI")]
    internal sealed class App : GraphicsApplication
    {
        public App()
        {
            // todo: hook into events
        }

        protected override void ParseArguments()
        {
            // placeholder
            using var stream = new FileStream("test.pgn", FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(stream, encoding: Encoding.UTF8);
            var pgn = PGN.Parse(reader.ReadToEnd());
            Console.WriteLine(pgn.Moves[^1].Position);
        }

        public override bool ShouldRunHeadless => true;
    }
}