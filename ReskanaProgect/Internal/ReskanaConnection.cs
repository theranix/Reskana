using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace ReskanaProgect.Internal
{
    public class ReskanaConnection
    {
        public readonly Socket Api;

        public ReskanaConnection(Socket api, bool udp = false)
        {
            Api = api;

            if (!udp)
            {
                Api.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                Api.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            //Usually, size of these buffers is about 64K
            Api.SendBufferSize = Math.Min(Math.Max(Api.SendBufferSize, Config.MinInternalBuffer), Config.SaeaBufferSize);
            Api.ReceiveBufferSize = Math.Min(Math.Max(Api.ReceiveBufferSize, Config.MinInternalBuffer), Config.SaeaBufferSize);
        }
    }
}
