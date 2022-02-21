using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReskanaProgect.Internal
{
    internal class StagingControl
    {
        /// <summary>
        /// Queue data and returns packetID
        /// </summary>
        public uint Track(in BufferSegmentStruct data)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// It removes packet from tracking
        /// </summary>
        public void Response(uint packetId)
        {

        }

        /// <summary>
        /// Check is packet in tracking. Returns true, if packet wasn't still delivered 
        /// </summary>
        public bool Peek(out BufferSegmentStruct data)
        {
            throw new NotImplementedException();
        }

    }
}
