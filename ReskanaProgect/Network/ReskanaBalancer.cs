using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReskanaProgect.Network
{
    public class ReskanaBalancer
    {
        public const int NumParallelConnections = 2;
        private ReskanaClient[] connections = new ReskanaClient[1];
        private object nextPacketLock = new object();

        /// <summary>
        /// 
        /// </summary>
        public bool AllowMalformedPackets = false;
        /// <summary>
        /// 
        /// </summary>
        public bool OrderedPackets = false;
        /// <summary>
        /// This event is synchronous!
        /// </summary>
        public event Action<BufferSegmentStruct> NextPacket;
        /// <summary>
        /// Client must be stopped manually, by calling method!
        /// </summary>
        public event Action<string> ConnectionWasBroken;
        /// <summary>
        /// Only for server
        /// </summary>
        internal IPAddress LastClientIP;

        /// <summary>
        /// Client
        /// </summary>
        public ReskanaBalancer()
        {
            connections[0] = new ReskanaClient();
        }

        /// <summary>
        /// Server
        /// </summary>
        public ReskanaBalancer(ReskanaClient initialConnection)
        {
            Configure(initialConnection);
            connections[0] = initialConnection;
            LastClientIP = initialConnection.RemoteAddress;
        }

        /// <summary>
        /// Server
        /// </summary>
        public void JoinConnection(ReskanaClient next)
        {
            Configure(next);
            var mArr = connections;
            var newArr = new ReskanaClient[mArr.Length + 1];
            for (int i = 0; i < mArr.Length; i++)
                newArr[i] = mArr[i];
            newArr[newArr.Length - 1] = next;
            connections = newArr;
        }


        private void Configure(ReskanaClient connection)
        {
            connection.NextPacket += data =>
            {
                lock (nextPacketLock)
                    NextPacket?.Invoke(data);
            };
            connection.ConnectionWasBroken += err =>
            {
                DisconnectInternal(connection, err.ToString());
            };
            connection.ConnectionWasBrokenByError += err =>
            {
                DisconnectInternal(connection, err.Message);
            };
            connection.ConnectionWasMalformed += () =>
            {
                if (AllowMalformedPackets)
                    return;
                DisconnectInternal(connection, "malformed");
            };
        }

        private void DisconnectInternal(ReskanaClient connection, string reason)
        {
            connection.Disconnect();
            connection.Dispose();

            //TEMP
            ConnectionWasBroken?.Invoke(reason);
        }

        public void DisconnectAll()
        {
            var mArr = connections;
            for (int i = 0; i < mArr.Length; i++)
            {
                mArr[i].Disconnect();
            }
        }

        public void Dispose()
        {
            var mArr = connections;
            for (int i = 0; i < mArr.Length; i++)
            {
                mArr[i].Dispose();
            }
            connections = new ReskanaClient[0];
        }

        public void Send(in BufferSegmentStruct data)
        {
            var mArr = connections;
            for (int i = 0; i < mArr.Length; i++)
            {
                if (!mArr[i].IsBusy)
                {
                    mArr[i].Send(data);
                    return;
                }
            }

            mArr[data.Length % mArr.Length].Send(data); //kind of random
        }

    }
}
