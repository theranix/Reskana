//(c) Качмар Сергей


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
    public unsafe class ConceptHelper
    {
        public bool MASTER = false;

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
            public long control;

            public InternalHeader(ushort totalLength, ushort hash, long control)
            {
                this.Reserve = 0;
                this.totalLength = totalLength;
                this.hash = hash;
                this.control = control;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 9)]
        struct ServicePacket
        {
            public const int length = 9;

            [FieldOffset(0)]
            public byte Reserve;
            [FieldOffset(1)]
            public long Operating;

            public ServicePacket(byte reserve, long operating)
            {
                Reserve = reserve;
                Operating = operating;
            }
        }

        struct Tracking
        {
            public BufferSegmentStruct Payload;
            public long Time;

            public Tracking(BufferSegmentStruct payload)
            {
                Payload = payload;
                Time = DateTime.Now.Ticks;
            }
        }


        private ConceptHelper remote;
        private long outcomingPacketOrder;
        private Random rand = new Random();
        private object iocpLock = new object();

        private Dictionary<long, Tracking> trackOutcoming = new Dictionary<long, Tracking>(10000);
        private ValueGraph pingCounter = new ValueGraph(20);
        private ValueGraph packetLoss = new ValueGraph(30);
        private ValueGraph overhead = new ValueGraph(30);
        private List<long> waitingPackets = new List<long>();
        private long rtoCheckerLimiter = DateTime.Now.Ticks;

        public event Action<BufferSegmentStruct> Received;
        public float OneWayPing => pingCounter.Avg;
        public float PacketLoss => packetLoss.Avg * packetLoss.Avg;
        public int OverheadBytes => (int)overhead.AvgInt;

        private ObjectPool<Byte[]> bufferPool = new ObjectPool<Byte[]>(() => new byte[20000], 100);
        private byte[] bufferService = new byte[ServicePacket.length];
        private BufferSegmentStruct receiveStaging = new BufferSegmentStruct(new byte[20000], 0, 0);
        private BufferSegmentStruct rtoStaging = new BufferSegmentStruct(new byte[20000], 0, 0);
        private List<BufferSegmentStruct> tempList = new List<BufferSegmentStruct>(4);

        public ConceptHelper()
        {

        }

        public void Bind(ConceptHelper remote)
        {
            this.remote = remote;
            waitingPackets.Add(0);
            pingCounter.Push(50); //init
        }



        public void Send(in BufferSegmentStruct data)
        {
            var packet = new BufferSegmentStruct(bufferPool.Get(), 0, data.Length + InternalHeader.length);
            var checksum = ComputeChecksum(data.Buffer, data.StartPosition, data.Length);
            Buffer.BlockCopy(data.Buffer, data.StartPosition, packet.Buffer, InternalHeader.length, data.Length);
            lock (iocpLock)
            {
                var packetId = outcomingPacketOrder++;
                fixed (byte* bufferRef = &packet.Buffer[0])
                {
                    *((InternalHeader*)(bufferRef)) = new InternalHeader((ushort)packet.Length, checksum, packetId);
                }
                trackOutcoming.Add(packetId, new Tracking(packet));
                if (trackOutcoming.Count > 100 || waitingPackets.Count > 1000)
                {

                }
                remote.IOCP(packet);
                RTOHelper();
            }
        }

        private void SendFast(ServicePacket packet)
        {
            fixed (byte* bufferRef = &bufferService[0])
            {
                *((ServicePacket*)(bufferRef)) = packet;
            }
            remote.IOCP(new BufferSegmentStruct(bufferService, 0, bufferService.Length));
        }

        public void IOCP(in BufferSegmentStruct data)
        {
            int Q_Ping = 10;
            int Q_Loss = 50;
            int Q_Duplication = 0;
            int Q_Fragmentation = 0;

            if (rand.Next(0, 100) > 100 - Q_Loss)
                return; //loss
            if (rand.Next(0, 100) > 100 - Q_Duplication)
                iocpInternal(data);
            iocpInternal(data);


            void iocpInternal(BufferSegmentStruct _data)
            {
                ThreadPool.UnsafeQueueUserWorkItem(x =>
                {
                    Thread.Sleep(Q_Ping);
                    lock (iocpLock)
                        try
                        {
                            if (rand.Next(0, 100) > 100 - Q_Fragmentation)
                            {
                                int separator = _data.Length / 2;
                                ReceiveNext(_data.Copy(0, separator));
                                ReceiveNext(_data.Copy(separator, _data.Length - separator));
                            }
                            else ReceiveNext(_data);
                        }
                        catch (Exception e)
                        {
                        }
                }, this);
            }
        }



        private void ReceiveNext(in BufferSegmentStruct portion)
        {
            Buffer.BlockCopy(portion.Buffer, 0, receiveStaging.Buffer, receiveStaging.Length, portion.Length);
            receiveStaging.Length += portion.Length;

            while (true)
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
                        SendFast(new ServicePacket(1, nextHeader.control));
                    SendFast(new ServicePacket(1, nextHeader.control));

                    var maxWaitingPacket = waitingPackets[waitingPackets.Count - 1];
                    if (nextHeader.control + 1 > maxWaitingPacket)
                    {
                        if (nextHeader.control - maxWaitingPacket > 100)
                        {
                            //TODO: Уязвиость протокола
                        }
                        for (int i = 0; i < nextHeader.control - maxWaitingPacket + 1; i++)
                            waitingPackets.Add(maxWaitingPacket + i + 1);
                    }
                    if (waitingPackets.Contains(nextHeader.control))
                    {
                        var checksum = ComputeChecksum(receiveStaging.Buffer, InternalHeader.length, nextHeader.totalLength - InternalHeader.length);
                        if (checksum != nextHeader.hash)
                        {
                            //TODO:
                        }

                        Received(receiveStaging.Copy(InternalHeader.length, nextHeader.totalLength - InternalHeader.length));
                        waitingPackets.Remove(nextHeader.control);
                        if (waitingPackets.Count == 0)
                            waitingPackets.Add(nextHeader.control + 1);
                        overhead.Push(0);
                    }
                    else
                    {
                        overhead.Push(nextHeader.totalLength);
                    }
                    readBytes = nextHeader.totalLength;
                }
                else
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
                        if (trackOutcoming.TryGetValue(nextHeader.Operating, out var tracking))
                        {
                            var diffMs = (nowTicks - tracking.Time) / TimeSpan.TicksPerMillisecond;
                            pingCounter.Push(diffMs / 2.0f);
                            trackOutcoming.Remove(nextHeader.Operating);
                            bufferPool.Return(tracking.Payload.Buffer);
                            packetLoss.Push(0f);
                        }
                    }
                    readBytes = ServicePacket.length;
                }

                if (receiveStaging.Length - readBytes != 0)
                    Buffer.BlockCopy(receiveStaging.Buffer, readBytes, receiveStaging.Buffer, 0,
                        receiveStaging.Length - readBytes);
                receiveStaging.Length -= readBytes;
            }
        }


        public void RTOHelper()
        {
            lock (iocpLock)
            {
                //При большой потере пакетов это овчень полезная коррекция, но немного повышает оверхэд
                var correntedOneWayPing = Math.Min(300, pingCounter.Avg * (1.0f - PacketLoss * 0.75f)); 

                var nowTicks = DateTime.Now.Ticks;
                if ((nowTicks - rtoCheckerLimiter) / TimeSpan.TicksPerMillisecond >
                    //MinRTO
                    correntedOneWayPing * 0.5f)
                {
                    rtoCheckerLimiter = nowTicks;
                    foreach (var item in trackOutcoming)
                    {
                        if ((nowTicks - item.Value.Time) / TimeSpan.TicksPerMillisecond > correntedOneWayPing * 2.5f + 1)
                        {
                            tempList.Add(item.Value.Payload);
                            //remote.IOCP(item.Value.Payload);
                            packetLoss.Push(1f);
                        }
                    }
                    int totalLength = 0;
                    int num = 0;
                    for (int i = 0; i < tempList.Count; i++)
                    {
                        if (totalLength + tempList[i].Length < 16000) //TODO: Здесь порция должна быть меньше, чем размер применого буфера раза в 2
                        {
                            totalLength += tempList[i].Length;
                            num++;
                        }
                    }
                    if (num == 1)
                    {
                        remote.IOCP(tempList[0]);
                    }
                    else if (num > 1)
                    {
                        rtoStaging.Length = 0;
                        for (int i = 0; i < num; i++)
                        {
                            Buffer.BlockCopy(tempList[i].Buffer, 0, rtoStaging.Buffer, rtoStaging.Length, tempList[i].Length);
                            rtoStaging.Length += tempList[i].Length;
                        }
                        remote.IOCP(rtoStaging);
                    }
                    tempList.Clear();
                }
            }
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
