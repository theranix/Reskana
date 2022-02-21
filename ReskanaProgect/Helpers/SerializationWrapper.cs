//(c) Качмар Сергей

using ManualPacketSerialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReskanaProgect.Helpers
{
    public class SerializationWrapper
    {

        private IReflectionSource table;



        public void Handler(in BufferSegment packet)
        {
            try
            {
                var pos = packet.StartPosition;
                DeltaStream.Unboxing(table, packet.Buffer, packet.Length, pos, out var result, 
                    Config.SaeaBufferSize < short.MaxValue);


            }
            catch (Exception e)
            {

            }
        }

    }
}
