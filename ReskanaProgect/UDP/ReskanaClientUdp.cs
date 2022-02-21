using ReskanaProgect.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ReskanaProgect.Network
{
    /// <summary>
    /// High-performance accruate and thread-safe Socket async I/O implementation
    /// </summary>
    public class ReskanaClientUdp : IDisposable
    {
        /// <summary>
        /// This event is synchronous!
        /// </summary>
        public event Action<BufferSegmentStruct> NextPacket;
        /// <summary>
        /// Client must be stopped manually, by calling method!
        /// </summary>
        public event Action<ReskanaError> ConnectionWasBroken;
        /// <summary>
        /// 
        /// </summary>
        public readonly bool IsAtServer;


        public bool IsStarted => isStarted;

        internal ReskanaConnection connection;
        internal ReskanaServerUdp server;
        internal IPEndPoint endPoint;

        private SocketAsyncEventArgs receiveSaea;
        private int asyncPollFlags = 0;
        private int connectionStatusFlags = 0;
        private bool isStarted;
        private object socketStatusLock = new object();
        private bool isDisposed;

        private RetransmissionController net;
        public RetransmissionController Network => net;


        public ReskanaClientUdp(IPEndPoint ep, ReskanaServerUdp server)
        {
            IsAtServer = true;
            this.server = server;
            this.endPoint = ep;

            isStarted = true;
            receiveSaea = new SocketAsyncEventArgs();
            receiveSaea.SetBuffer(new byte[Config.SaeaBufferSize], 0, Config.SaeaBufferSize);
            receiveSaea.Completed += OnReceived;
            /*receiveSaea = //server.ringSaeaPool.Get();
            receiveSaea.Completed += OnReceived;*/

            net = new RetransmissionController(this.SendInternal, x => NextPacket?.Invoke(x));
        }

        public ReskanaClientUdp(IPEndPoint ep)
        {
            this.endPoint = ep;
            IsAtServer = false;
            receiveSaea = new SocketAsyncEventArgs();
            receiveSaea.SetBuffer(new byte[Config.SaeaBufferSize], 0, Config.SaeaBufferSize);
            receiveSaea.Completed += OnReceived;

            net = new RetransmissionController(this.SendInternal, x => NextPacket?.Invoke(x));
        }

        /// <summary>
        /// Can lead to exception
        /// </summary>
        public bool TryConnect(Socket useApi)
        {
            lock (socketStatusLock)
            {
                this.connection = new ReskanaConnection(useApi ?? new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp), true);
                //connection.Api.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                isStarted = true;

                /*if (!IsAtServer)
                {
                    connection.Api.Bind(new IPEndPoint(IPAddress.Any, endPoint.Port));
                }*/

                connection.Api.ReceiveTimeout = 3000;
                int numRetries = 3000;

                while (numRetries-- > 0)
                {
                    try
                    {
                        if (!IsAtServer)
                        {
                            var result = connection.Api.SendTo(new byte[1] { 255 }, endPoint);
                            if (result != 1)
                                throw new SocketException();
                            //254
                            EndPoint localEp = endPoint;
                            var response = connection.Api.ReceiveFrom(new byte[1], ref localEp); //endPoint will be set as remote address
                            endPoint = localEp as IPEndPoint;
                        }
                        else
                        {
                            //connection.Api.Connect(endPoint);
                            connection.Api.SendTo(new byte[] { 254 }, endPoint);
                            connection.Api.SendTo(new byte[] { 254 }, endPoint);
                        }
                    }
                    catch (Exception e)
                    {
                        continue;
                    }
                    if (numRetries == 0)
                        return false;
                    break;
                }
                receiveSaea.RemoteEndPoint = endPoint;
                connectionStatusFlags = 0;
                return true;
            }
        }

        /// <summary>
        /// Probably sending data will not be sent
        /// </summary>
        public void Disconnect(bool waitForSaea = true)
        {
            lock (socketStatusLock)
            {
                if (!isStarted)
                    return;
                isStarted = false;
            }
            //connection.Api.Shutdown(SocketShutdown.Both); - this method is too slow and not necessary
            //connection.Api.Disconnect(false); - this method is too slow and not necessary
            lock (socketStatusLock)
                connection.Api.Close();

            while (waitForSaea)//We should be sure that saea's are not used
            {
                lock (socketStatusLock)
                    if (asyncPollFlags == 0)
                        break;
                Thread.Sleep(0);
            }
        }

        public void StartReceiving()
        {
            try
            {
                bool sync;
                lock (socketStatusLock)
                {
                    sync = !connection.Api.ReceiveFromAsync(receiveSaea);
                    asyncPollFlags++;
                }

                if (sync)
                    OnReceived(this, receiveSaea);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException e) //async I/O errors
            {
                if (Config.InternalErrorsLogger != null)
                    Config.InternalErrorsLogger("Reskana: InvalidOperation during I/O: " + e.Message);
            }
        }


        private unsafe void OnReceived(object e, SocketAsyncEventArgs saea)
        {
            lock (socketStatusLock)
                asyncPollFlags--;
            if (saea.SocketError != SocketError.Success)
            {
                lock (socketStatusLock)
                {
                    if (!isStarted || connectionStatusFlags >= 1 || Config.IsProblemSerious(saea.SocketError))
                    {
                        if (isStarted)
                            ConnectionWasBroken?.Invoke(ReskanaError.SocketError);
                        return;
                    }
                }
                connectionStatusFlags++;
                StartReceiving(); //Try again
                return;
            }

            int available = saea.Offset + saea.BytesTransferred;
            var err = net.ReceiveNext(new BufferSegmentStruct(saea.Buffer, 0, available));
            if (err != ReskanaError.None)
            {
                ConnectionWasBroken?.Invoke(err);
                return;
            }
            saea.SetBuffer(0, Config.SaeaBufferSize);
            //if (saea.BytesTransferred > 0 && isStarted) //isStarted can be changed with calling Disconnect from NextPacket
            //    saea.SetBuffer(available, Config.SaeaBufferSize - available);
            StartReceiving();
        }

        public unsafe void Send(in BufferSegmentStruct data)
        {
            lock (socketStatusLock)
            {
                if (!isStarted)
                    return;
            }
            net.Send(data);

            /*sendSaea.UserToken = data.Length + InternalHeader.length;
            PutPacket(data, sendSaea.Buffer, 0, control);
            sendSaea.SetBuffer(0, data.Length + InternalHeader.length);
            StartSending();*/
        }

        private void SendInternal(BufferSegmentStruct data)
        {
            lock (socketStatusLock)
            {
                int b = 0;
                try
                {
                    //Среднее время: 0.02 мс
                    //Среднее время async-версии не отличается, но имеет более высокий оверхэд
                    b = connection.Api.SendTo(data.Buffer, data.StartPosition, data.Length, SocketFlags.None, endPoint);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception e)
                {
                    //b : 0
                }

                if (b > 0)
                {
                    data.Length -= b;
                    data.StartPosition += b;
                    if (data.Length > 0)
                        SendInternal(data);
                }
                else
                {
                    if (connectionStatusFlags >= 1)
                    {
                        if (isStarted)
                            ConnectionWasBroken?.Invoke(ReskanaError.SocketError);
                        return;
                    }
                    connectionStatusFlags++;
                    SendInternal(data); //try again
                }
            }
        }

        public bool RTOHelper()
        {
            lock (socketStatusLock)
                if (!isStarted)
                    return false;

            var err = net.RTOHelper();
            if (err != ReskanaError.None)
            {
                ConnectionWasBroken?.Invoke(err);
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            lock (socketStatusLock)
            {
                if (isDisposed)
                    return;
                isDisposed = true;
            }
            if (IsAtServer)
            {
                receiveSaea.Completed -= OnReceived;
                //server.ringSaeaPool.Return(receiveSaea);
                //server.ringSaeaPool.Return(sendSaea);
            }
        }
    }
}
