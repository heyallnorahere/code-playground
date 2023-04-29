using System;

namespace CodePlayground.Graphics
{
    public enum DeviceType
    {
        Discrete,
        Integrated,
        Virtual,
        CPU,
        Other
    }

    public interface IGraphicsDeviceInfo
    {
        public string Name { get; }
        public DeviceType Type { get; }
    }

    public interface IGraphicsDeviceScorer
    {
        public int ScoreDevice(IGraphicsDeviceInfo device, IGraphicsContext context);
    }

    public interface IGraphicsDevice
    {
        public IGraphicsDeviceInfo DeviceInfo { get; }
        public IGraphicsQueue GetQueue(GraphicsQueueFlags usage);
    }
}
