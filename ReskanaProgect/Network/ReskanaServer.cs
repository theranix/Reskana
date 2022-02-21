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
    /// <summary>
    /// High-performance accruate and thread-safe async server implementation, including pool
    /// </summary>
    public class ReskanaServer : IDisposable
    {
        /// <summary>
        /// This is async event
        /// </summary>
        public event Action<ReskanaClient> NewClientConnected;
        /// <summary>
        /// 
        /// </summary>
        public bool IsListening => isListening;
        /// <summary>
        /// 
        /// </summary>
        public Func<IPAddress, bool> IpChecker;
        /// <summary>
        /// 
        /// </summary>
        public volatile bool AllowNewConnections = true;
        /// <summary>
        /// 
        /// </summary>
        public readonly int ApproxOnlineParam;

        private Socket listener;
        internal ObjectPool<SocketAsyncEventArgs> ringSaeaPool;
        private bool isListening;
        private object syncRoot = new object();
        private EndPoint bindTo;

        //private ConcurrentDictionary<IPAddress, ReskanaClient> activeClient;

        /// <summary>
        /// Can provide exception (Socket.Bind)
        /// </summary>
        public ReskanaServer(int approxOnline, EndPoint bindTo)
        {
            this.ApproxOnlineParam = approxOnline;
            this.bindTo = bindTo;
            ringSaeaPool = new ObjectPool<SocketAsyncEventArgs>(() =>
            {
                var saea = new SocketAsyncEventArgs();
                saea.SetBuffer(new byte[Config.SaeaBufferSize], 0, Config.SaeaBufferSize);
                return saea;
            }, 2 * approxOnline);
            //activeClient = new ConcurrentDictionary<IPAddress, ReskanaClient>();
        }

        public void Start()
        {
            lock (syncRoot)
            {
                if (isListening)
                    return;
                isListening = true;
            }

            var saea = new SocketAsyncEventArgs();
            saea.Completed += FetchBySaea;
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(bindTo);
            listener.Listen(Config.BacklogSize);
            ThreadPool.QueueUserWorkItem(x => Iteration((SocketAsyncEventArgs)x), saea);
        }

        private void Iteration(SocketAsyncEventArgs saea)
        {
            lock (syncRoot)
            {
                if (!isListening)
                    return;

                if (!listener.AcceptAsync(saea))
                    FetchBySaea(this, saea);
            }
        }

        private void FetchBySaea(object e, SocketAsyncEventArgs saea)
        {
            if (saea.SocketError != SocketError.SocketError && saea.AcceptSocket != null)
            {
                try
                {
                    FetchConnection(saea.AcceptSocket);
                }
                catch (Exception ex)
                {
                    saea.AcceptSocket.Close(0);
                    if (Config.InternalErrorsLogger != null)
                        Config.InternalErrorsLogger("Reskana: Error while FetchConnection");
                }
                saea.AcceptSocket = null;
            }

            Iteration(saea);
        }

        public void Stop()
        {
            lock (syncRoot)
            {
                listener.Close();
                isListening = false;
            }
        }

        private void FetchConnection(Socket newClient)
        {
            var ip = ((IPEndPoint)newClient.RemoteEndPoint).Address;
            /*var client = activeClients.AddOrUpdate(ip,
                x =>
                {
                    var newClient = new ReskanaClient(new RescanaConnection(newClient), this);
                    NewClientConnected?.Invoke(newClient);
                    return newClient;
                },
                (x, y) =>
                {
                    y.ChangeConnection(new RescanaConnection(newClient));
                    return y;
                });*/

            if ((IpChecker == null || IpChecker(ip)) && AllowNewConnections)
            {
                ThreadPool.QueueUserWorkItem(x =>
                {
                    var client = new ReskanaClient(new ReskanaConnection(newClient), this);
                    NewClientConnected?.Invoke(client);
                });
            }
            else
            {
                newClient.Close(0);
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (isListening)
                    throw new InvalidOperationException("You must call stop");
                listener.Close(0);
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
