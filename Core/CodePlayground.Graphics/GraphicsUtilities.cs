namespace CodePlayground.Graphics
{
    public static class GraphicsUtilities
    {
        public static unsafe void CopyFromCPU<T>(this IDeviceBuffer buffer, T[] data) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyFromCPU(ptr, data.Length * sizeof(T));
            }
        }

        public static unsafe void CopyToCPU<T>(this IDeviceBuffer buffer, T[] data) where T : unmanaged
        {
            fixed (T* ptr = data)
            {
                buffer.CopyToCPU(ptr, data.Length * sizeof(T));
            }
        }
    }
}
