using CodePlayground;
using CodePlayground.Graphics;

namespace MetalTest.iOS
{
    [ApplicationTitle("MetalTest")]
    internal sealed class App : GraphicsApplication
    {
        public static int Main(string[] args) => RunApplication<App>(args);

        public App()
        {
            Load += OnLoad;
            Closing += OnClose;
        }

        private void OnLoad()
        {
            CreateGraphicsContext();
        }

        private void OnClose()
        {
            GraphicsContext?.Dispose();
        }
    }
}