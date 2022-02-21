using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace ReskanaProgect
{
    public class Config
    {
        public const int SaeaBufferSize = 8192 + 8192 + 4096;
        public const int MinInternalBuffer = 8192;
        public const int BacklogSize = 10;
        public const int SendQueueGrow = 4096;

        //1492 or int.MaxValue (disable)
        public const int MTU = int.MaxValue;//1492;

        internal static bool IsProblemSerious(SocketError error)
        {
            return 
                error == SocketError.OperationAborted ||
                error == SocketError.ConnectionAborted ||
                error == SocketError.ConnectionRefused ||
                error == SocketError.ConnectionReset ||
                error == SocketError.NotConnected ||
                error == SocketError.Shutdown;
        }

        public static Action<string> InternalErrorsLogger;
    }
}
