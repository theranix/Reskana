using ReskanaProgect.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TheraEngine;

namespace ReskanaProgect
{
    public enum ReskanaError
    {
        None,
        ConnectionTimeout,
        ChecksumOrOrderError,
        SocketError,
    }

    public unsafe class RetransmissionController
    {
        [StructLayout(LayoutKind.Explicit, Size = 13)]
        struct InternalHeader
        {
            public const int length = 13;

            [FieldOffset(0)]
            public byte Reserve;
            [FieldOffset(1)]
            public ushort totalLength;
            [FieldOffset(3)]
            public ushort hash;
            [FieldOffset(5)]
            public int control;
            [FieldOffset(9)]
            public byte frame;
            [FieldOffset(10)]
            public byte framesCount;
            [FieldOffset(11)]
            public ushort wholePacketId;


            public InternalHeader(ushort totalLength, ushort hash, int control, byte frame, byte framesCount, ushort wholePacketId)
            {
                this.Reserve = 0;
                this.totalLength = totalLength;
                this.hash = hash;
                this.control = control;
                this.frame = frame;
                this.wholePacketId = wholePacketId;
                this.framesCount = framesCount;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 5)]
        struct ServicePacket
        {
            public const int length = 5;

            [FieldOffset(0)]
            public byte Reserve;
            [FieldOffset(1)]
            public int Operating;

            public ServicePacket(byte reserve, int operating)
            {
                Reserve = reserve;
                Operating = operating;
            }
        }

        struct Tracking
        {
            public readonly BufferSegmentStruct Payload;
            public readonly long Time;

            public Tracking(BufferSegmentStruct payload)
            {
                Payload = payload;
                Time = DateTime.Now.Ticks;
            }
        }

        class BuildingPacket
        {
            public readonly byte[] Buffer;
            public int MaxLength;
            public int NumFrames;

            public BuildingPacket(byte[] buffer)
            {
                Buffer = buffer;
                MaxLength = 0;
                NumFrames = 1;
            }
        }


        private object dataLock = new object();
        private int outcomingPacketOrder;
        private ushort outcomingPacketOrder2;
        private Dictionary<int, Tracking> trackOutcoming = new Dictionary<int, Tracking>(10000);
        private Dictionary<ushort, BuildingPacket> buildingPackets = new Dictionary<ushort, BuildingPacket>(10000);
        private long rtoCheckerLimiter = DateTime.Now.Ticks;
        private long lastReceivedPacket = DateTime.Now.Ticks;

        private Action<BufferSegmentStruct> send;
        private Action<BufferSegmentStruct> receive;

        private ValueGraph pingCounter = new ValueGraph(20);
        private ValueGraph packetLoss = new ValueGraph(30);
        private ValueGraph overhead = new ValueGraph(30);
        private List<int> waitingPackets = new List<int>();

        private ObjectPool<Byte[]> bufferPool = new ObjectPool<Byte[]>(() => new byte[Config.SaeaBufferSize], 10); //TODO: Нужен пул размера окна, тк иногда пакеты не могут быть больше если здан MTU
        private byte[] bufferService = new byte[ServicePacket.length];
        private BufferSegmentStruct receiveStaging = new BufferSegmentStruct(new byte[Config.SaeaBufferSize], 0, 0);
        private BufferSegmentStruct rtoStaging = new BufferSegmentStruct(new byte[Config.SaeaBufferSize], 0, 0);
        private List<BufferSegmentStruct> tempList = new List<BufferSegmentStruct>(4);

        public float OneWayPing => pingCounter.Avg;
        public float PacketLoss => packetLoss.Avg * packetLoss.Avg;
        public int OverheadBytes => (int)overhead.AvgInt;

        public RetransmissionController(Action<BufferSegmentStruct> send, Action<BufferSegmentStruct> receive)
        {
            this.send = send;
            this.receive = receive;
            waitingPackets.Add(0);
            pingCounter.Push(50); //init
        }

        /// <summary>
        /// This method can be called from ANY threads
        /// </summary>
        public void Send(in BufferSegmentStruct data)
        {
            var packet = new BufferSegmentStruct(bufferPool.Get(), 0, data.Length + InternalHeader.length);
            int remain = data.Length;
            byte frame = 0;
            byte framesCount = (byte)(data.Length / (Config.MTU - InternalHeader.length));
            if (framesCount * (Config.MTU - InternalHeader.length) < data.Length)
                framesCount++;
            ushort wholePacketId = 0;

            while (remain > 0)
            {
                int startPos = data.StartPosition + data.Length - remain;
                int count = Math.Min(remain, Config.MTU - InternalHeader.length);

                var checksum = ComputeChecksum(data.Buffer, startPos, count);
                Buffer.BlockCopy(data.Buffer, startPos, packet.Buffer, InternalHeader.length, count);
                lock (dataLock)
                {
                    if (frame == 0)
                        wholePacketId = outcomingPacketOrder2++;
                    var packetId = outcomingPacketOrder == int.MaxValue ? 0 : (outcomingPacketOrder++);
                    fixed (byte* bufferRef = &packet.Buffer[0])
                    {
                        *((InternalHeader*)(bufferRef)) = new InternalHeader((ushort)(count+ InternalHeader.length), checksum, packetId, 
                            remain == count && frame == 0 ? byte.MaxValue : (frame++), framesCount,
                            wholePacketId);
                    }
                    trackOutcoming.Add(packetId, new Tracking(packet));
                }
                packet.Length = count + InternalHeader.length;
                send(packet);
                remain -= count;
            }
            RTOHelper();
        }

        private void SendSrv(ServicePacket packet)
        {
            fixed (byte* bufferRef = &bufferService[0])
            {
                *((ServicePacket*)(bufferRef)) = packet;
            }
            send(new BufferSegmentStruct(bufferService, 0, bufferService.Length));
        }

        /// <summary>
        /// This method can be called from SINGLE thread
        /// </summary>
        public ReskanaError ReceiveNext(in BufferSegmentStruct portion)
        {
            Buffer.BlockCopy(portion.Buffer, 0, receiveStaging.Buffer, receiveStaging.Length, portion.Length);
            receiveStaging.Length += portion.Length;

            while (receiveStaging.Length > 0)
            {
                var nowTicks = DateTime.Now.Ticks;
                int readBytes = 0;
                if (receiveStaging.Buffer[0] == 0)
                {
                    if (receiveStaging.Length < InternalHeader.length)
                        break;
                    InternalHeader nextHeader;
                    fixed (byte* bufferRef = &receiveStaging.Buffer[0])
                    {
                        nextHeader = *((InternalHeader*)(bufferRef));
                    }
                    if (receiveStaging.Length < nextHeader.totalLength)
                        break;

                    if (PacketLoss > 0.3f)
                        SendSrv(new ServicePacket(1, nextHeader.control));
                    SendSrv(new ServicePacket(1, nextHeader.control));

                    var maxWaitingPacket = waitingPackets[waitingPackets.Count - 1];
                    if (nextHeader.control + 1 > maxWaitingPacket)
                    {
                        if (nextHeader.control - maxWaitingPacket > 100)
                        {
                            return ReskanaError.ChecksumOrOrderError;
                        }
                        for (int i = 0; i < nextHeader.control - maxWaitingPacket + 1; i++)
                            waitingPackets.Add((maxWaitingPacket + i) == int.MaxValue ? 0 : (maxWaitingPacket + i + 1));
                    }
                    if (waitingPackets.Contains(nextHeader.control))
                    {
                        var checksum = ComputeChecksum(receiveStaging.Buffer, InternalHeader.length, nextHeader.totalLength - InternalHeader.length);
                        if (checksum != nextHeader.hash)
                        {
                            return ReskanaError.ChecksumOrOrderError;
                        }

                        Defragmentation(
                            nextHeader,
                            receiveStaging.Copy(InternalHeader.length, nextHeader.totalLength - InternalHeader.length));
                            //new BufferSegmentStruct(receiveStaging.Buffer, InternalHeader.length, nextHeader.totalLength - InternalHeader.length));
                        //receive(receiveStaging.Copy(InternalHeader.length, nextHeader.totalLength - InternalHeader.length));
                        waitingPackets.Remove(nextHeader.control);
                        if (waitingPackets.Count == 0)
                            waitingPackets.Add(nextHeader.control == int.MaxValue ? 0 : (nextHeader.control + 1));
                        overhead.Push(0);
                    }
                    else
                    {
                        overhead.Push(nextHeader.totalLength);
                    }
                    readBytes = nextHeader.totalLength;
                }
                else if (receiveStaging.Buffer[0] == 1)
                {
                    if (receiveStaging.Length < ServicePacket.length)
                        break;
                    ServicePacket nextHeader;
                    fixed (byte* bufferRef = &receiveStaging.Buffer[0])
                    {
                        nextHeader = *((ServicePacket*)(bufferRef));
                    }

                    if (nextHeader.Reserve == 1)
                    {
                        lock (dataLock)
                        {
                            lastReceivedPacket = nowTicks;
                            if (trackOutcoming.TryGetValue(nextHeader.Operating, out var tracking))
                            {
                                var diffMs = (nowTicks - tracking.Time) / TimeSpan.TicksPerMillisecond;
                                pingCounter.Push(diffMs / 2.0f);
                                trackOutcoming.Remove(nextHeader.Operating);
                                bufferPool.Return(tracking.Payload.Buffer);
                                packetLoss.Push(0f);
                            }
                        }
                    }
                    readBytes = ServicePacket.length;
                }
                else
                {
                    //Some connection packet received here? (254, 255) (for example - duplication)
                    readBytes = 1;
                }

                if (receiveStaging.Length - readBytes != 0)
                    Buffer.BlockCopy(receiveStaging.Buffer, readBytes, receiveStaging.Buffer, 0,
                        receiveStaging.Length - readBytes);
                receiveStaging.Length -= readBytes;
            }

            return ReskanaError.None;
        }

        private void Defragmentation(in InternalHeader header, BufferSegmentStruct data)
        {
            if (header.frame == 255) //single packet with length less than MTU
            {
                receive(data);
                return;
            }

            BufferSegmentStruct? toReceive = null;
            int expectedPosition = (Config.MTU - InternalHeader.length) * header.frame;
            int maxLength = expectedPosition + data.Length;

            lock (dataLock)
            {
                if (buildingPackets.TryGetValue(header.wholePacketId, out var builder))
                {
                    Buffer.BlockCopy(data.Buffer, data.StartPosition, builder.Buffer,
                        expectedPosition,
                        data.Length);
                    builder.MaxLength = Math.Max(builder.MaxLength, maxLength);
                    builder.NumFrames++;

                    if (builder.NumFrames == header.framesCount)
                    {
                        toReceive = new BufferSegmentStruct(builder.Buffer, 0, builder.MaxLength);
                        buildingPackets.Remove(header.wholePacketId);
                    }
                }
                else
                {
                    var localBuffer = bufferPool.Get();
                    Buffer.BlockCopy(data.Buffer, data.StartPosition, localBuffer,
                        expectedPosition, 
                        data.Length);
                    buildingPackets.Add(header.wholePacketId, new BuildingPacket(localBuffer) { MaxLength = maxLength, NumFrames = 1 });
                }
            }

            if (toReceive.HasValue)
            {
                receive(toReceive.Value);
            }
        }

        /// <summary>
        /// This method can be called from SINGLE thread
        /// </summary>
        public ReskanaError RTOHelper()
        {
            lock (dataLock)
            {
                //При большой потере пакетов это овчень полезная коррекция, но немного повышает оверхэд
                var correntedOneWayPing = Math.Min(300, pingCounter.Avg * (1.0f - PacketLoss * 0.75f)); 
                var nowTicks = DateTime.Now.Ticks;
                if (nowTicks - lastReceivedPacket > TimeSpan.TicksPerSecond * 7)
                {
                    return ReskanaError.ConnectionTimeout;
                }

                if ((nowTicks - rtoCheckerLimiter) / TimeSpan.TicksPerMillisecond >
                    correntedOneWayPing * 0.5f)
                {
                    rtoCheckerLimiter = nowTicks;
                    foreach (var item in trackOutcoming)
                    {
                        if ((nowTicks - item.Value.Time) / TimeSpan.TicksPerMillisecond > correntedOneWayPing * 2.5f + 1)
                        {
                            tempList.Add(item.Value.Payload);
                            packetLoss.Push(1f);
                        }
                    }
                    int totalLength = 0;
                    int num = 0;
                    for (int i = 0; i < tempList.Count; i++)
                    {
                        if (totalLength + tempList[i].Length < Math.Max(Config.SaeaBufferSize / 2, tempList[i].Length))
                        {
                            totalLength += tempList[i].Length;
                            num++;
                        }
                    }
                    if (num == 1)
                    {
                        send(tempList[0]);
                    }
                    else if (num > 1)
                    {
                        rtoStaging.Length = 0;
                        for (int i = 0; i < num; i++)
                        {
                            Buffer.BlockCopy(tempList[i].Buffer, 0, rtoStaging.Buffer, rtoStaging.Length, tempList[i].Length);
                            rtoStaging.Length += tempList[i].Length;
                        }
                        send(rtoStaging);
                    }
                    tempList.Clear();
                }
            }

            return ReskanaError.None;
        }


        public unsafe static ushort ComputeChecksum(byte[] data, int startIndex, int count)
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
    }
}
