//(c) Качмар Сергей


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReskanaProgect
{
    public struct BufferSegmentStruct
    {
        public byte[] Buffer;
        public int StartPosition;
        public int Length;

        public BufferSegmentStruct(byte[] buffer, int startPosition, int length)
        {
            Buffer = buffer;
            StartPosition = startPosition;
            Length = length;
        }

        public BufferSegmentStruct Copy(int startIndex, int count)
        {
            var output = new byte[count];
            System.Buffer.BlockCopy(Buffer, startIndex, output, 0, count);
            return new BufferSegmentStruct(output, 0, count);
        }

    }
}
