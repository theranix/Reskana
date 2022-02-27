//(c) Качмар Сергей


using LiteNetLib;
using LiteNetLib.Utils;
using ReskanaProgect.TCP;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TheraEngine;

namespace ReskanaProgect
{
    class Program
    {
        static void Main(string[] args)
        {
            var serverEp = new IPEndPoint(IPAddress.Parse("192.168.1.77"), 27777);
            var any = new IPEndPoint(IPAddress.Any, 27777);


            /* var a = new ConceptHelper() { MASTER = true };
             var b = new ConceptHelper();
             a.Bind(b);
             b.Bind(a);
             int pps1 = 0;
             var tt = Stopwatch.StartNew();
             var tt2 = Stopwatch.StartNew();

             a.Received += x =>
             {
                 pps1++;
                 if (tt.ElapsedMilliseconds > 100)
                 {
                     Console.Title = "pps " + (long)(pps1 / tt2.Elapsed.TotalSeconds) + 
                     ", ping: " + Math.Round(a.OneWayPing, 1) + 
                     ", loss: " + Math.Round(a.PacketLoss, 1) + 
                     ", overhead: " + a.OverheadBytes + "b";
                     tt.Restart();
                 }
                 a.Send(x); //Echo
             };
             b.Received += x =>
             {
                 b.Send(x); //Echo
             };
             //b.Send(new BufferSegmentStruct() { Buffer = new byte[] { 1, 2, 3 }, Length = 3 });
             b.Send(new BufferSegmentStruct() { Buffer = new byte[128], Length = 128 });

             new Thread(x =>
             {
                 while (true)
                 {
                     a.RTOHelper();
                     b.RTOHelper();
                     Thread.Sleep(16);
                 }
             }).Start();

             Console.ReadLine();*/



            bool useSERVER = true;
            bool useCLIENT = true;



            /*if (useSERVER)
            {
                var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                EndPoint localEp = any;
                var listenerBuffer = new byte[128];
                listener.Bind(any);

                while (true)
                {
                    var c = listener.ReceiveFrom(listenerBuffer, ref localEp);
                    if (c > 0)
                    {
                        var clientAtServer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        //clientAtServer.Connect(localEp);

                        clientAtServer.SendTo(new byte[5], localEp);
                        //var xx = clientAtServer.ReceiveFrom(new byte[5], ref localEp);
                    }
                }
            }

            if (useCLIENT)
            {
                var client1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                EndPoint localEp = new IPEndPoint(IPAddress.Parse("185.91.55.22"), 27777);

                var clientAtServer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                clientAtServer.ReceiveTimeout = 1000;

                client1.Bind(new IPEndPoint(IPAddress.Any, 27777));
                clientAtServer.SendTo(new byte[5], localEp);
                clientAtServer.SendTo(new byte[5], localEp);

                while (true)
                {
                    try
                    {
                        var xx = clientAtServer.ReceiveFrom(new byte[5], ref localEp);
                        Console.WriteLine("Read bytes: " + xx);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("-");
                    }
                }
            }

            Console.ReadLine();*/





            Config.InternalErrorsLogger = x => Console.WriteLine(x);

            if (useSERVER)
            {
                var pssLock = new object();
                var tt = Stopwatch.StartNew();
                var tt2 = Stopwatch.StartNew();
                var udpServer = new ReskanaServerUdp(10, new IPEndPoint(IPAddress.Any, 27777));

                new Thread(() =>
                {
                    while (true)
                    {
                        if (tt.ElapsedMilliseconds > 100)
                        {
                            var ping = new ValueCounter();
                            var loss = new ValueCounter();
                            var overhead = 0L;
                            var total = 0L;
                            var pps1 = 0L;
                            foreach (var item in udpServer.clients)
                            {
                                pps1 += item.Value.Network.TotalSendReceivePackets;
                                ping.Push(item.Value.Network.OneWayPing);
                                loss.Push(item.Value.Network.PacketLoss);
                                overhead += item.Value.Network.OverheadBytes;
                                total += item.Value.Network.TotalReceivedBytes * 2;
                            }

                            Console.Title = "pps " + (long)(pps1 / tt2.Elapsed.TotalSeconds) +
                                ", ping: " + Math.Round(ping.Avg, 1) +
                                ", loss: " + Math.Round(loss.Avg, 1) +
                                ", overhead: " + (overhead / 1024) + "kb" +
                                ", payload: " + (total / 1024) + "kb" +
                                ", mb/s: " + (int)(total / 1024 / 1024 / tt2.Elapsed.TotalSeconds) +
                                ", overhead: " + Math.Round(overhead * 100.0 / total) + "%";
                            tt.Restart();
                        }

                        var rt = Stopwatch.StartNew();
                        foreach (var item in udpServer.clients)
                        {
                            if (!item.Value.RTOHelper())
                            {
                                //TODO
                            }
                        }
                        rt.Stop();
                        Thread.Sleep(Math.Max(0, 16 - (int)rt.ElapsedMilliseconds));
                        if (rt.ElapsedMilliseconds > 16)
                            Console.WriteLine("Server-side overhead ms: " + rt.ElapsedMilliseconds);
                    }
                }).Start();

                udpServer.NewClientConnected += (x) =>
                {
                    Console.WriteLine("Client connected " + x.endPoint);
                    x.NextPacket += (packet) =>
                    {
                        x.Send(packet);
                    };
                    if (x.TryConnect(udpServer.Test))
                    {
                        x.StartReceiving();
                    }
                };
                udpServer.Start();
            }

            if (useCLIENT)
            {
                const int numClients = 10;
                const int numPacketsLifetime = int.MaxValue;//10000;
                int currentClients = 0;
                object locker2 = new object();
                var clientsClients = new ConcurrentBag<ReskanaClientUdp>();

                new Thread(x =>
                {
                    while (true)
                    {
                        var rt = Stopwatch.StartNew();
                        foreach (var item in clientsClients)
                        {
                            item.RTOHelper();
                        }
                        rt.Stop();
                        Thread.Sleep(Math.Max(0, 16 - (int)rt.ElapsedMilliseconds));
                        if (rt.ElapsedMilliseconds > 16)
                            Console.WriteLine("Server-side overhead ms: " + rt.ElapsedMilliseconds);
                    }
                }).Start();

                while (true)
                {
                    int q = numPacketsLifetime;

                    var udpClient = new ReskanaClientUdp(new IPEndPoint(IPAddress.Parse("192.168.1.77"), 27777));
                    //var udpClient = new ReskanaClientUdp(new IPEndPoint(IPAddress.Parse("194.169.160.240"), 27777));

                    udpClient.NextPacket += (packet) =>
                    {
                        udpClient.Send(packet);
                        if (q-- == 0)
                        {
                            udpClient.Disconnect(false);
                            lock (locker2)
                            {
                                currentClients--;
                                //TODO clientsClients.Remove
                            }
                        }
                    };

                    if (udpClient.TryConnect(null))
                    {
                        clientsClients.Add(udpClient);
                        udpClient.StartReceiving();
                        udpClient.Send(new BufferSegment()
                        {
                            Buffer = Enumerable.Range(0, 5000).Select(x => (byte)(x % 256)).ToArray(),
                            Length = 5000
                        });
                        lock (locker2)
                            currentClients++;
                    }
                    else
                    {
                        Console.WriteLine("Connect error");
                    }

                    while (currentClients >= numClients)
                    {
                        Thread.Sleep(10);
                    }
                    Thread.Sleep(50);
                }
            }

            Console.ReadLine();

















            
            var response1 = Encoding.UTF32.GetBytes("Received!");
            var sendBuffer2 = new BufferSegment(response1, 0, response1.Length);

            var server = new ReskanaServer(100, new IPEndPoint(IPAddress.Any, 27777));
            int f = 0;
            server.NewClientConnected += (c) =>
            {
                c.NextPacket += (packet) =>
                {
                    /* if (f++ >= 100000)
                     { 
                         c.Disconnect();
                         c.Dispose();
                         return;
                     }*/

                    if (packet.Buffer[packet.StartPosition] == 0)
                    {

                    }
                    else
                    {
                        //Console.WriteLine(Encoding.UTF32.GetString(packet.Buffer, packet.StartPosition, packet.Length));
                    }
                    c.Send(sendBuffer2);
                };
                c.ConnectionWasBroken += x =>
                {
                    c.Disconnect();
                    c.Dispose();
                };
                c.ConnectionWasMalformed += () =>
                {
                    Console.WriteLine("server-ConnectionWasMalformed");
                };
                c.StartReceiving();
            };
            server.Start();


            var random = new Random();

            /*TestClient2(client =>
            {
                Parallel.For(0, 10, x =>
                {
                    if (x == 5)
                    {
                        client.Disconnect();
                    }
                    else
                    {
                        var data = new byte[random.Next(15, 1000)];
                        random.NextBytes(data);
                        client.Send(new BufferSegmentStruct(data, 0, data.Length));
                    }
                });
            });

            Console.ReadLine();
            */

            long trhougput = 0;
            int pps = 0;
            int online = 0;
            var time = Stopwatch.StartNew();
            var locker = new object();
            while (true)
            {
                if (online < 500)
                    new Thread(() =>
                        {
                            online++;
                            var client = new ReskanaClient();
                            client.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.77"), 27777));
                            var receivedLock = new object();
                            var received = false;

                            client.NextPacket += (packet) =>
                            {
                                lock(receivedLock)
                                    received = true;
                                //Console.WriteLine("Received");
                            };
                            client.ConnectionWasBroken += x =>
                            {
                                client.Disconnect();
                                Console.WriteLine("Disconnected! " + x);
                            };
                            client.ConnectionWasMalformed += () =>
                            {
                                Console.WriteLine("client-ConnectionWasMalformed");
                            };
                            client.StartReceiving();


                            while (true)
                            {
                                var data = new byte[random.Next(15, 1000)];
                                random.NextBytes(data);

                                received = false;
                                client.Send(new BufferSegment(data, 0, data.Length));
                                client.Send(new BufferSegment(data, 0, data.Length));

                                /*int t = 4;
                                Parallel.For(0, 4, x =>
                                {
                                    client.Send(new BufferSegmentStruct(data, 0, data.Length));
                                    client.Send(new BufferSegmentStruct(data, 0, data.Length));
                                });*/

                                int j = 0;
                                while (true)
                                {
                                    lock (receivedLock)
                                    {
                                        if (received)
                                            break;
                                    }
                                    j++;
                                    Thread.Sleep(j);
                                    if (j > 10000)
                                    {
                                        Console.WriteLine("Timeout!");
                                        break;
                                    }
                                }

                                /*if (!sm.Wait(10000))
                                {
                                    Console.WriteLine("Timeout!");
                                    ff++;
                                    if (ff > 5)
                                    {

                                    }
                                    goto start;
                                }
                                sm.Set();*/

                                //client.Disconnect();
                                //client.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.77"), 27777));
                                //client.StartReceiving();

                                lock (locker)
                                {
                                    trhougput += data.Length; //* t * 2;
                                    pps += 1;//t * 2;
                                    if (random.Next(0, 1000) >= 997)
                                    {
                                        client.Disconnect();
                                        client.Dispose();
                                        online--;
                                        break;
                                    }
                                }
                            }
                        }).Start(); //Thread
                Thread.Sleep(200);

                lock (locker)
                {
                    Console.Clear();
                    Console.WriteLine("Online: " + online);
                    Console.WriteLine("Pps: " + (int)(pps / time.Elapsed.TotalSeconds));
                    Console.WriteLine("Throughput (mb/s): " + (int)(trhougput / 1024 / 1024 / time.Elapsed.TotalSeconds));
                    pps = 0;
                    trhougput = 0;
                    time.Restart();
                }
            }

            Console.ReadLine();
        }


        private static void TestClient2(Action<ReskanaClient> body)
        {
            var client = new ReskanaClient();
            var sm = new ManualResetEventSlim();

            client.NextPacket += (packet) =>
            {
                sm.Set();
                Console.WriteLine("Received");
            };

            client.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.77"), 27777));
            client.StartReceiving();

            body(client);

            client.Disconnect();
        }

    }
}
