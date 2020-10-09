// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Sockets.Tests
{
    public class CloseTests
    {
        [Fact]
        public static void Close()
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.Close();
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public static void Close_Timeout(int timeout)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.Close(timeout);
            }
        }

        [Fact]
        public static void Close_BadTimeout_Throws()
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => s.Close(-2));
            }
        }

        private static (Socket client, Socket server) GetConnectedTcpSockets()
        {
            using (Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);

                var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clientSocket.Connect(listener.LocalEndPoint);
                Socket serverSocket = listener.Accept();

                serverSocket.NoDelay = true;
                clientSocket.NoDelay = true;

                return (clientSocket, serverSocket);
            }
        }

        private static async Task DoReceive(string name, Socket socket)
        {
            await Task.Run(() =>
            {
                try
                {
                    byte[] buffer = new byte[1];

                    int bytesRead = socket.Receive(buffer);
                    Console.WriteLine($"[{name}]: Receive returned {bytesRead}");
                }
                catch (Exception e)
                {
                    SocketError? socketError = null;
                    if (e is SocketException se)
                    {
                        socketError = se.SocketErrorCode;
                    }

                    Console.WriteLine($"[{name}]: Caught exception, SocketErrorCode={socketError}: {e}");
                }
            });
        }

        private static async Task DoReceiveAsync(string name, Socket socket)
        {
            await Task.Run(async () =>
            {
                try
                {
                    byte[] buffer = new byte[1];

                    int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None);
                    Console.WriteLine($"[{name}]: Receive returned {bytesRead}");
                }
                catch (Exception e)
                {
                    SocketError? socketError = null;
                    if (e is SocketException se)
                    {
                        socketError = se.SocketErrorCode;
                    }

                    Console.WriteLine($"[{name}]: Caught exception, SocketErrorCode={socketError}: {e}");
                }
            });
        }

        [Fact]
        public async Task CloseWithPendingSyncOperations()
        {
            const string name = "SyncClose";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceive(name + " Client:", client);
            Task t2 = DoReceive(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        [Fact]
        public async Task CloseWithPendingAsyncOperations()
        {
            const string name = "AsyncClose";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceiveAsync(name + " Client:", client);
            Task t2 = DoReceiveAsync(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        [Fact]
        public async Task ShutdownWithPendingSyncOperations()
        {
            const string name = "SyncShutdown";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceive(name + " Client:", client);
            Task t2 = DoReceive(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Shutdown");
            client.Shutdown(SocketShutdown.Both);

            Task t3 = DoReceive(name + " ClientAfterShutdown", client);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        [Fact]
        public async Task ShutdownWithPendingAsyncOperations()
        {
            const string name = "AsyncShutdown";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceiveAsync(name + " Client:", client);
            Task t2 = DoReceiveAsync(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Shutdown");
            client.Shutdown(SocketShutdown.Both);

            Task t3 = DoReceiveAsync(name + " ClientAfterShutdown", client);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        private static extern unsafe bool CancelIoEx(IntPtr handle, NativeOverlapped* lpOverlapped);

        private static unsafe bool DoCancel(Socket s)
        {
            return CancelIoEx(s.Handle, null);
        }

        [Fact]
        public async Task CancelWithPendingSyncOperations()
        {
            const string name = "SyncCancel";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceive(name + " Client:", client);
            Task t2 = DoReceive(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Cancel");
            DoCancel(client);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        [Fact]
        public async Task CancelWithPendingAsyncOperations()
        {
            const string name = "AsyncCancel";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceiveAsync(name + " Client:", client);
            Task t2 = DoReceiveAsync(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Cancel");
            DoCancel(client);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        [Fact]
        public async Task ShutdownCancelWithPendingSyncOperations()
        {
            const string name = "SyncShutdownCancel";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceive(name + " Client:", client);
            Task t2 = DoReceive(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Shutdown");
            client.Shutdown(SocketShutdown.Both);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Cancel");
            DoCancel(client);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        [Fact]
        public async Task ShutdownCancelWithPendingAsyncOperations()
        {
            const string name = "AsyncShutdownCancel";

            (Socket client, Socket server) = GetConnectedTcpSockets();

            Console.WriteLine($"[{name}]: Starting up");

            Task t1 = DoReceiveAsync(name + " Client:", client);
            Task t2 = DoReceiveAsync(name + " Server:", server);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Shutdown");
            client.Shutdown(SocketShutdown.Both);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Cancel");
            DoCancel(client);

            Thread.Sleep(2000);

            Console.WriteLine($"[{name}]: About to Close");
            client.Close();

            await t1;
            await t2;
        }

        // TODO:
        // Cases:
        // (1) Just Close
        // (2) Shutdown, then Close
        // (3) CancelIoEx, then Close
        // (4) Shutdown then Cancel, or vice versa
        // Similar for linux
        // Show both local and remote behavior for pending receive
        // Also, both sync and async
    }
}
