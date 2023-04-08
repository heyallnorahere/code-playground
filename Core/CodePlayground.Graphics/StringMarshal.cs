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
        }

        ~StringMarshal() => FreeMemory();
        public void Dispose() => FreeMemory();

        public byte* MarshalString(string value)
        {
            nint pointer = Marshal.StringToHGlobalAnsi(value);
            mPointers.Add(pointer);

            return (byte*)pointer;
        }

        public void FreeMemory()
        {
            mPointers.ForEach(Marshal.FreeHGlobal);
            mPointers.Clear();
        }

        private readonly List<nint> mPointers;
    }
}