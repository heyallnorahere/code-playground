using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CodePlayground.Graphics
{
    internal unsafe sealed class StringMarshal : IDisposable
    {
        public StringMarshal()
        {
            mPointers = new List<nint>();
            mStringIndices = new Dictionary<string, int>();
        }

        ~StringMarshal() => FreeMemory();
        public void Dispose() => FreeMemory();

        public byte* MarshalString(string value)
        {
            if (mStringIndices.TryGetValue(value, out int index))
            {
                return (byte*)mPointers[index];
            }

            nint pointer = Marshal.StringToHGlobalAnsi(value);

            mStringIndices.Add(value, mPointers.Count);
            mPointers.Add(pointer);

            return (byte*)pointer;
        }

        public void FreeMemory()
        {
            mPointers.ForEach(Marshal.FreeHGlobal);
            mPointers.Clear();
            mStringIndices.Clear();
        }

        private readonly List<nint> mPointers;
        private readonly Dictionary<string, int> mStringIndices;
    }
}