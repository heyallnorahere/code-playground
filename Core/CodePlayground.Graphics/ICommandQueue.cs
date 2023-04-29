using System;

namespace CodePlayground.Graphics
{
    [Flags]
    public enum CommandQueueFlags
    {
        Graphics = 0x1,
        Compute = 0x2,
        Transfer = 0x4
    }

    public interface ICommandList
    {
        public bool IsRecording { get; }
        public void Begin();
        public void End();
    }

    public interface ICommandQueue
    {
        public CommandQueueFlags Usage { get; }
        public int CommandListCap { get; set; }

        public ICommandList Release();
        public void Submit(ICommandList commandList);
        public void Wait();
    }
}
