//(c) Качмар Сергей


using ReskanaProgect.Internal;
using ReskanaProgect.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ReskanaProgect.Network
{
    public class ReskanaServerUdp
    {
        /// <summary>
        /// This is async event
        /// </summary>
        public event Action<ReskanaClientUdp> NewClientConnected;
        /// <summary>
        /// 
        /// </summary>
        public bool IsListening => isListening;
        /// <summary>
        /// 
        /// </summary>
        public Func<IPAddress, bool> IpChecker;

        private Socket listener;
        private bool isListening;
        private object syncRoot = new object();
        private IPEndPoint listenFrom;

        public Socket Test => listener;

        public ReskanaServerUdp(int approxOnline, IPEndPoint listen)
        {
            listenFrom = listen;
        }

        public void Start()
        {
            lock (syncRoot)
            {
                if (isListening)
                    return;
                isListening = true;
            }

            listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.ReuseAddress, true);
            listener.Bind(listenFrom);

            var saea = new SocketAsyncEventArgs();
            saea.SetBuffer(new byte[128], 0, 128);
            saea.Completed += FetchBySaea;
            saea.RemoteEndPoint = listenFrom;
            ThreadPool.QueueUserWorkItem(x => Iteration((SocketAsyncEventArgs)x), saea);
        }

        private void Iteration(SocketAsyncEventArgs saea)
        {
            lock (syncRoot)
            {
                if (!isListening)
                    return;

                if (!listener.ReceiveFromAsync(saea))
                    FetchBySaea(this, saea);
            }
        }

        private void FetchBySaea(object e, SocketAsyncEventArgs saea)
        {
            if (saea.SocketError != SocketError.SocketError && saea.Buffer[0] == 255)
            {
                try
                {
                    FetchConnection(saea.RemoteEndPoint as IPEndPoint);//.ReceiveMessageFromPacketInfo.Address);
                }
                catch (Exception ex)
                {
                    if (Config.InternalErrorsLogger != null)
                        Config.InternalErrorsLogger("ReskanaUDP: Error while FetchConnection");
                }
            }

            //TODO: temporary dis
            //Iteration(saea);
        }

        public void Stop()
        {
            lock (syncRoot)
            {
                listener.Close();
                isListening = false;
            }
        }

        private void FetchConnection(IPEndPoint ep)
        {
            if (IpChecker == null || IpChecker(ep.Address))
            {
                ThreadPool.QueueUserWorkItem(x =>
                {
                    var client = new ReskanaClientUdp(ep, this);
                    NewClientConnected?.Invoke(client);
                });
            }
        }

        /*private void Accepted(IPAddress ip, ConnectionStatus connection)
        {
            activeClients.AddOrUpdate(ip,
                x =>
                {
                    var client = new ReskanaClient(connection, this);
                    NewClientConnected?.Invoke(client);
                    return client;
                },
                (x, y) =>
                {
                    y.ChangeConnection(connection);
                    return y;
                });
        }

        internal void Disconnect(ReskanaClient client)
        {
            if (activeClients.TryRemove(client.CurrentConnection.endpoint.Address, out var c))
            {

            }
        }*/


    }
}
