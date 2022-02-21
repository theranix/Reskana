//(c) Качмар Сергей


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReskanaProgect
{
    public struct BufferSegment
    {
        public byte[] Buffer;
        public int StartPosition;
        public int Length;

        public BufferSegment(byte[] buffer, int startPosition, int length)
        {
            Buffer = buffer;
            StartPosition = startPosition;
            Length = length;
        }

        public BufferSegment Copy(int startIndex, int count)
        {
            var output = new byte[count];
            System.Buffer.BlockCopy(Buffer, startIndex, output, 0, count);
            return new BufferSegment(output, 0, count);
        }

    }
}
