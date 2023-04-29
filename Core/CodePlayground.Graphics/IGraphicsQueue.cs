using System;

namespace CodePlayground.Graphics
{
    [Flags]
    public enum GraphicsQueueFlags
    {
        Graphics = 0x1,
        Compute = 0x2,
        Transfer = 0x4
    }

    public interface IGraphicsQueue
    {
        public GraphicsQueueFlags Usage { get; }
    }
}
