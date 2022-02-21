//(c) Качмар Сергей


using ReskanaProgect.Internal;
using System;
using System.Collections.Generic;
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
    public class ReskanaClient : IDisposable
    {
        /// <summary>
        /// This event is synchronous!
        /// </summary>
        public event Action<BufferSegmentStruct> NextPacket;
        /// <summary>
        /// Client must be stopped manually, by calling method!
        /// </summary>
        public event Action<SocketError> ConnectionWasBroken;
        /// <summary>
        /// Client must be stopped manually, by calling method!
        /// </summary>
        public event Action<Exception> ConnectionWasBrokenByError;
        /// <summary>
        /// You can stop or not to stop client
        /// </summary>
        public event Action ConnectionWasMalformed;
        /// <summary>
        /// 
        /// </summary>
        public readonly bool IsAtServer;

        public IPAddress RemoteAddress => ((IPEndPoint)(connection.Api.RemoteEndPoint)).Address;


        public bool IsBusy
        {
            get
            {
                lock (socketStatusLock)
                    if (asyncPollFlagsSendOnly == 0)
                        return false;
                return true;
            }
        }

        public bool IsStarted => isStarted;

        internal ReskanaConnection connection;
        internal ReskanaServer server;

        private SocketAsyncEventArgs sendSaea;
        private SocketAsyncEventArgs receiveSaea;
        private BufferSegment sendQueue = new BufferSegment() { Buffer = new byte[Config.SendQueueGrow] };
        private int sendPollFlags = 0;
        private int asyncPollFlags = 0;
        private int asyncPollFlagsSendOnly = 0;
        private object sendQueueLock = new object();
        private int connectionStatusFlags = 0;
        private bool isStarted;
        private bool isBroken;
        private object socketStatusLock = new object();
        private bool isDisposed;
        private byte packetOrder = 0, packetOrderRemote = 0;


        public ReskanaClient(ReskanaConnection connection, ReskanaServer server)
        {
            IsAtServer = true;
            this.connection = connection;
            this.server = server;

            isStarted = true;
            sendSaea = server.ringSaeaPool.Get();
            receiveSaea = server.ringSaeaPool.Get();
            sendSaea.Completed += SendCompleted;
            receiveSaea.Completed += OnReceived;
        }

        public ReskanaClient()
        {
            IsAtServer = false;
            sendSaea = new SocketAsyncEventArgs();
            receiveSaea = new SocketAsyncEventArgs();
            sendSaea.SetBuffer(new byte[Config.SaeaBufferSize], 0, Config.SaeaBufferSize);
            receiveSaea.SetBuffer(new byte[Config.SaeaBufferSize], 0, Config.SaeaBufferSize);
            sendSaea.Completed += SendCompleted;
            receiveSaea.Completed += OnReceived;
        }

        /// <summary>
        /// Can lead to exception
        /// </summary>
        public void Connect(IPEndPoint endPoint)
        {
            if (IsAtServer)
                throw new InvalidOperationException();

            lock (sendQueueLock)
            {
                connection = new ReskanaConnection(new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Stream,
                    ProtocolType.Tcp), false);
                isStarted = true;
                connection.Api.Connect(endPoint);

                packetOrder = 0;
                packetOrderRemote = 0;
                connectionStatusFlags = 0;
                sendPollFlags = 0;
                sendQueue.Length = 0;
            }
        }

        /// <summary>
        /// Probably sending data will not be sent
        /// </summary>
        public void Disconnect(bool waitForSaea = true)
        {
            lock (sendQueueLock)
            {
                if (isStarted)
                {
                    isStarted = false;
                    //connection.Api.Shutdown(SocketShutdown.Both); - this method is too slow and not necessary
                    //connection.Api.Disconnect(false); - this method is too slow and not necessary
                    lock (socketStatusLock)
                        connection.Api.Close();

                    //Сокеты непредсказуемы, поэтому может случиться так, что мы вообще не получим 
                    //OnCompleted у Saea
                    var i = 0;
                    var timeout = 100; //ms
                    while (waitForSaea)//We should be sure that saea's are not used
                    {
                        lock (socketStatusLock)
                            if (asyncPollFlags == 0)
                                break;
                        Thread.Sleep(i++);
                        timeout -= i;
                        if (timeout <= 0)
                        {
                            isBroken = true;
                            break;
                        }
                    }
                    //connection.Api.LingerState.Enabled = false;
                }
            }
        }

        public void StartReceiving()
        {
            try
            {
                bool sync;
                lock (socketStatusLock) //cannot concurrent send & read
                {
                    sync = !connection.Api.ReceiveAsync(receiveSaea);
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

        private void StartSending()
        {
            try
            {
                bool sync;
                lock (socketStatusLock)
                {
                    sync = !connection.Api.SendAsync(sendSaea);
                    asyncPollFlags++;
                    asyncPollFlagsSendOnly++;
                }

                if (sync)
                    SendCompleted(this, sendSaea);
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
                lock (sendQueueLock)
                {
                    if (!isStarted || connectionStatusFlags >= 1 || Config.IsProblemSerious(saea.SocketError))
                    {
                        if (isStarted)
                            ConnectionWasBroken?.Invoke(saea.SocketError);
                        return;
                    }
                }
                connectionStatusFlags++;
                StartReceiving(); //Try again
                return;
            }

            int available = saea.Offset + saea.BytesTransferred;
            while (available > InternalHeader.length)
            {
                var buffer = saea.Buffer;
                InternalHeader nextHeader;
                fixed (byte* bufferRef = &buffer[0])
                {
                    nextHeader = *((InternalHeader*)(bufferRef));
                }

                if (available >= nextHeader.totalLength)
                {
                    var hash = FxtcpComputeChecksum(buffer, InternalHeader.length, nextHeader.totalLength - InternalHeader.length);
                    if (hash == nextHeader.hash &&
                        packetOrderRemote == nextHeader.control)
                    {
                        packetOrderRemote++;
                        var packet = new BufferSegmentStruct(
                            buffer,
                            InternalHeader.length,
                            nextHeader.totalLength - InternalHeader.length);

                        lock (sendQueueLock)
                        {
                            if (isStarted) //Status can be changed (disconnected) that lead to return SAEA to pool
                                try
                                {
                                    NextPacket?.Invoke(packet);
                                }
                                catch (Exception ex)
                                {
                                    ConnectionWasBrokenByError?.Invoke(ex);
                                    if (Config.InternalErrorsLogger != null)
                                        Config.InternalErrorsLogger("Reskana client NextPacket error: " + ex.Message);
                                }
                        }
                    }
                    else
                    {
                        ConnectionWasMalformed?.Invoke();
                        if (hash == nextHeader.hash)
                        {
                            packetOrderRemote = nextHeader.control;
                            packetOrderRemote++;
                        }
                    }

                    available -= nextHeader.totalLength;
                    if (available > 0)
                        Buffer.BlockCopy(buffer, nextHeader.totalLength, buffer, 0, available);
                }
                else break;
            }

            if (saea.BytesTransferred > 0 && isStarted) //isStarted can be changed with calling Disconnect from NextPacket
                saea.SetBuffer(available, Config.SaeaBufferSize - available);

            StartReceiving();
        }

        public unsafe void Send(in BufferSegmentStruct data)
        {
            byte control = 0;
            lock (sendQueueLock)
            {
                if (!isStarted)
                    return;

                control = packetOrder++;
                if (sendPollFlags == 1)
                {
                    if (sendQueue.Length + data.Length + InternalHeader.length > sendQueue.Buffer.Length)
                        Array.Resize(ref sendQueue.Buffer, sendQueue.Length + data.Length + Config.SendQueueGrow);
                    PutPacket(data, sendQueue.Buffer, sendQueue.Length, control);
                    sendQueue.Length += data.Length + InternalHeader.length;
                    return;
                }
                sendPollFlags = 1;
            }

            sendSaea.UserToken = data.Length + InternalHeader.length;
            PutPacket(data, sendSaea.Buffer, 0, control);
            sendSaea.SetBuffer(0, data.Length + InternalHeader.length);
            StartSending();
        }

        private unsafe void PutPacket(in BufferSegmentStruct data, byte[] dst, int dstStart, byte control)
        {
            //Header:
            var header = new InternalHeader(
                (ushort)(data.Length + InternalHeader.length),
                FxtcpComputeChecksum(data.Buffer, data.StartPosition, data.Length),
                control);
            fixed (byte* bufferRef = &dst[dstStart])
            {
                *((InternalHeader*)(bufferRef)) = header;
            }
            //Data:
            Buffer.BlockCopy(data.Buffer, data.StartPosition, dst, dstStart + InternalHeader.length, data.Length);
        }


        private void SendCompleted(object e, SocketAsyncEventArgs saea)
        {
            lock (socketStatusLock)
            {
                asyncPollFlags--;
                asyncPollFlagsSendOnly--;
            }
            if (saea.SocketError != SocketError.Success)
            {
                lock (sendQueueLock)
                {
                    if (connectionStatusFlags >= 1 || Config.IsProblemSerious(saea.SocketError))
                    {
                        if (isStarted)
                            ConnectionWasBroken?.Invoke(saea.SocketError);
                        return;
                    }
                    connectionStatusFlags++;
                }
                StartSending(); //try again
                return;
            }

            var remain = (int)saea.UserToken;
            remain -= saea.BytesTransferred;
            if (remain == 0)
            {
                lock (sendQueueLock)
                {
                    //Success sent
                    if (sendQueue.Length == 0)
                    {
                        sendPollFlags = 0;
                    }
                    else
                    {
                        var takeBytes = Math.Min(Config.SaeaBufferSize, sendQueue.Length);
                        saea.SetBuffer(0, takeBytes);
                        Buffer.BlockCopy(sendQueue.Buffer, 0, saea.Buffer, 0, takeBytes);
                        var rest = sendQueue.Length - takeBytes;
                        if (rest > 0)
                            Buffer.BlockCopy(sendQueue.Buffer, takeBytes, sendQueue.Buffer, 0, rest);
                        sendQueue.Length = rest;
                        remain = takeBytes;
                    }
                }
            }
            else
            {
                saea.SetBuffer(saea.Offset + saea.BytesTransferred, saea.Count - saea.BytesTransferred);
            }

            if (remain > 0)
            {
                saea.UserToken = remain;
                StartSending();
            }
        }

        public void Dispose()
        {
            lock (sendQueueLock)
            {
                if (isDisposed)
                    return;
                isDisposed = true;
            }
            if (IsAtServer)
            {
                try
                {
                    receiveSaea.Completed -= OnReceived;
                    sendSaea.Completed -= SendCompleted;
                }
                finally
                {
                }

                if (!isBroken)
                {
                    server.ringSaeaPool.Return(receiveSaea);
                    server.ringSaeaPool.Return(sendSaea);
                }
            }
        }

        public unsafe static ushort FxtcpComputeChecksum(byte[] data, int startIndex, int count)
        {
            int numberOfLoops = (count >> 2) - 1; // div 4
            int remainingBytes = count % 4; // mod 4
            uint hash = 31;
            unchecked
            {
                fixed (byte* pPtr = &data[startIndex])
                {
                    for (; numberOfLoops >= 0; numberOfLoops--)
                        //hash = (hash << 7) ^ *((uint*)(pPtr + numberOfLoops * 4));
                        hash = 2 * hash + *((uint*)(pPtr + numberOfLoops * 4));
                    for (int i = 0; i < remainingBytes; i++)
                        //hash = hash ^ (uint)pPtr[i];
                        hash = 2 * hash + (uint)(pPtr[i]);
                }
                hash ^= hash << 3;
                hash += hash >> 5;
                hash ^= hash << 4;
                hash += hash >> 17;
                hash ^= hash << 25;
                hash += hash >> 6;
            }
            return (ushort)(hash % ushort.MaxValue);
        }


        [StructLayout(LayoutKind.Explicit, Size = 5)]
        struct InternalHeader
        {
            public const int length = 5;

            [FieldOffset(0)]
            public ushort @totalLength;
            [FieldOffset(2)]
            public ushort @hash;
            [FieldOffset(4)]
            public byte @control;

            public InternalHeader(ushort totalLength, ushort hash, byte control)
            {
                this.totalLength = totalLength;
                this.hash = hash;
                this.control = control;
            }
        }
    }
}
