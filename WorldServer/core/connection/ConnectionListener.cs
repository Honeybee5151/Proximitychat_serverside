using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WorldServer.networking;

using WorldServer.utils;

namespace WorldServer.core.connection
{
    #region Tokens

    public sealed class SendToken
    {
        public readonly int BufferOffset;

        public int BytesAvailable;
        public int BytesSent;
        public byte[] Data;

        public SendToken(int offset)
        {
            BufferOffset = offset;
            Data = new byte[0x100000];
        }

        public void Reset()
        {
            BytesAvailable = 0;
            BytesSent = 0;
        }
    }

    public sealed class ReceiveToken
    {
        public const int PrefixLength = 5;

        public readonly int BufferOffset;

        public int BytesRead;
        public byte[] PacketBytes;
        public int PacketLength;

        public ReceiveToken(int offset)
        {
            BufferOffset = offset;
            PacketBytes = new byte[ConnectionListener.BUFFER_SIZE];
            PacketLength = PrefixLength;
        }

        public byte[] GetPacketBody()
        {
            if (BytesRead < PrefixLength)
                throw new Exception("Packet prefix not read yet.");

            var packetBody = new byte[PacketLength - PrefixLength];
            Array.Copy(PacketBytes, PrefixLength, packetBody, 0, packetBody.Length);
            return packetBody;
        }

        public MessageId GetPacketId()
        {
            if (BytesRead < PrefixLength)
                throw new Exception("Packet id not read yet.");
            return (MessageId)PacketBytes[4];
        }

        public void Reset()
        {
            PacketLength = PrefixLength;
            BytesRead = 0;
        }
    }

    public enum SendState
    {
        Awaiting,
        Ready,
        Sending
    }

    #endregion Tokens

    public sealed class ConnectionListener
    {
        public const int BUFFER_SIZE = ushort.MaxValue * 3;
        private const int BACKLOG = 100;
        private const int MAX_SIMULTANEOUS_ACCEPT_OPS = 10;
        private const int OPS_TO_PRE_ALLOCATE = 2;

        private GameServer GameServer;
        private BufferManager BuffManager;
        private ClientPool ClientPool;
        private SocketAsyncEventArgsPool EventArgsPoolAccept;
        private Semaphore MaxConnectionsEnforcer;
        //777592
        private VoiceHandler voiceHandler;

        public ConnectionListener(GameServer gameServer)
        {
            GameServer = gameServer;

            Port = GameServer.Configuration.serverInfo.port;
            MaxConnections = GameServer.Configuration.serverSettings.maxConnections;

            BuffManager = new BufferManager((MaxConnections + 1) * BUFFER_SIZE * OPS_TO_PRE_ALLOCATE, BUFFER_SIZE);
            EventArgsPoolAccept = new SocketAsyncEventArgsPool(MAX_SIMULTANEOUS_ACCEPT_OPS);
            ClientPool = new ClientPool(MaxConnections + 1);
            MaxConnectionsEnforcer = new Semaphore(MaxConnections, MaxConnections);
            //777592
            voiceHandler = new VoiceHandler(gameServer);
            //777592
            Port2 = 2051;
            
        }

        private Socket ListenSocket { get; set; }
        private int MaxConnections { get; }
        private int Port { get; }
        //777592
        private int Port2 { get; }
        private Socket ListenSocket2 { get; set; }

        public void Initialize()
        {
            BuffManager.InitBuffer();

            for (var i = 0; i < MAX_SIMULTANEOUS_ACCEPT_OPS; i++)
                EventArgsPoolAccept.Push(CreateNewAcceptEventArgs());

            for (var i = 0; i < MaxConnections + 1; i++)
            {
                var send = CreateNewSendEventArgs();
                var receive = CreateNewReceiveEventArgs();
                ClientPool.Push(new Client(this, GameServer, send, receive));
            }
        }

        public void Start()
        {
            var localEndPoint = new IPEndPoint(IPAddress.Any, Port);
            ListenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ListenSocket.Bind(localEndPoint);
            ListenSocket.Listen(BACKLOG);

            StartAccept();
            
            var localEndPoint2 = new IPEndPoint(IPAddress.Any, Port2);
            ListenSocket2 = new Socket(localEndPoint2.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ListenSocket2.Bind(localEndPoint2);
            ListenSocket2.Listen(BACKLOG);
            //777592
            
            StartAccept2();
            //777592
            _ = Task.Run(StartVoiceListener);
        }

        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e) => ProcessAccept(e);

        private SocketAsyncEventArgs CreateNewAcceptEventArgs()
        {
            var acceptEventArg = new SocketAsyncEventArgs();
            acceptEventArg.Completed += AcceptEventArg_Completed;
            return acceptEventArg;
        }
        //777592
        private async Task StartVoiceListener()
        {
            Console.WriteLine("=== DEBUG: StartVoiceListener called! ===");
    
            try 
            {
                Console.WriteLine("=== DEBUG: Creating TcpListener on port 2051 ===");
                var voiceListener = new TcpListener(IPAddress.Any, 2051);
        
                Console.WriteLine("=== DEBUG: Starting TcpListener ===");
                voiceListener.Start();
        
                Console.WriteLine("=== DEBUG: Voice listener started successfully! ===");

                while (true)  // Use ListenSocket as condition
                {
                    var client = await voiceListener.AcceptTcpClientAsync();
                    Console.WriteLine($"Voice client connected from {client.Client.RemoteEndPoint}");
                    _ = Task.Run(() => voiceHandler.HandleVoiceClient(client));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== DEBUG: Voice listener FAILED: {ex.Message} ===");
            }
        }
        private SocketAsyncEventArgs CreateNewReceiveEventArgs()
        {
            var eventArgs = new SocketAsyncEventArgs();
            BuffManager.SetBuffer(eventArgs);
            eventArgs.UserToken = new ReceiveToken(eventArgs.Offset);
            return eventArgs;
        }

        private SocketAsyncEventArgs CreateNewSendEventArgs()
        {
            var eventArgs = new SocketAsyncEventArgs();
            BuffManager.SetBuffer(eventArgs);
            eventArgs.UserToken = new SendToken(eventArgs.Offset);
            return eventArgs;
        }

        private void HandleBadAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            acceptEventArgs.AcceptSocket.Close();
            EventArgsPoolAccept.Push(acceptEventArgs);
        }

        public void Disable()
        {
            Console.WriteLine("[ConnectionListener] Disabled");
            try
            {
                ListenSocket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e)
            {
                if (!(e is SocketException se) || se.SocketErrorCode != SocketError.NotConnected)
                    StaticLogger.Instance.Error(e);
            }
            ListenSocket.Close();
        }

        private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            if (acceptEventArgs.SocketError != SocketError.Success)
            {
                StartAccept();
                HandleBadAccept(acceptEventArgs);
                return;
            }

            acceptEventArgs.AcceptSocket.NoDelay = true;
            ClientPool.Pop().SetSocket(acceptEventArgs.AcceptSocket);

            acceptEventArgs.AcceptSocket = null;
            EventArgsPoolAccept.Push(acceptEventArgs);

            StartAccept();
        }
//777592
        private void StartAccept2()
        {
            SocketAsyncEventArgs acceptEventArg;

            if (EventArgsPoolAccept.Count > 1)
                try
                {
                    acceptEventArg = EventArgsPoolAccept.Pop();
                }
                catch
                {
                    acceptEventArg = CreateNewAcceptEventArgs2(); // Use separate event args for port 2051
                }
            else
                acceptEventArg = CreateNewAcceptEventArgs2(); // Use separate event args for port 2051

            _ = MaxConnectionsEnforcer.WaitOne();

            try
            {
                var willRaiseEvent = ListenSocket2.AcceptAsync(acceptEventArg); // Use ListenSocket2
                if (!willRaiseEvent)
                    ProcessAccept2(acceptEventArg); // Use separate process method
            }
            catch
            {
            }
        }

//777592
        private SocketAsyncEventArgs CreateNewAcceptEventArgs2()
        {
            var acceptEventArg = new SocketAsyncEventArgs();
            acceptEventArg.Completed += AcceptEventArg2_Completed; // Separate event handler
            return acceptEventArg;
        }

//777592
        private void AcceptEventArg2_Completed(object sender, SocketAsyncEventArgs e) => ProcessAccept2(e);

//777592
        private void ProcessAccept2(SocketAsyncEventArgs acceptEventArgs)
        {
            if (acceptEventArgs.SocketError != SocketError.Success)
            {
                StartAccept2(); // Restart accept on port 2051
                HandleBadAccept(acceptEventArgs);
                return;
            }

            acceptEventArgs.AcceptSocket.NoDelay = true;
    
            // Handle voice connection differently - pass to voice handler
            _ = Task.Run(() => voiceHandler.HandleVoiceClient(new TcpClient { Client = acceptEventArgs.AcceptSocket }));

            acceptEventArgs.AcceptSocket = null;
            EventArgsPoolAccept.Push(acceptEventArgs);

            StartAccept2(); // Continue accepting on port 2051
        }
        private void StartAccept()
        {
            SocketAsyncEventArgs acceptEventArg;

            if (EventArgsPoolAccept.Count > 1)
                try
                {
                    acceptEventArg = EventArgsPoolAccept.Pop();
                }
                catch
                {
                    acceptEventArg = CreateNewAcceptEventArgs();
                }
            else
                acceptEventArg = CreateNewAcceptEventArgs();

            _ = MaxConnectionsEnforcer.WaitOne();

            try
            {
                var willRaiseEvent = ListenSocket.AcceptAsync(acceptEventArg);
                if (!willRaiseEvent)
                    ProcessAccept(acceptEventArg);
            }
            catch
            {
            }
        }

        #region Disconnect - Shutdown

        public void Disconnect(Client client)
        {
            try
            {
                client.Socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e)
            {
                var se = e as SocketException;
                if (se == null || se.SocketErrorCode != SocketError.NotConnected)
                    StaticLogger.Instance.Error($"{se.Message} {se.StackTrace}");
            }

            client.Socket.Close();
            client.Reset();

            ClientPool.Push(client);

            try
            {
                MaxConnectionsEnforcer.Release();
            }
            catch (SemaphoreFullException e)
            {
                // This should happen only on server restart
                // If it doesn't need to handle the problem somwhere else
                StaticLogger.Instance.Error($"MaxConnectionsEnforcer.Release(): {e.StackTrace}");
            }
        }

        public void Shutdown()
        {
            foreach (var client in GameServer.ConnectionManager.Clients)
                client.Key.Disconnect("Shutdown Server");

            while (EventArgsPoolAccept.Count > 0)
            {
                var eventArgs = EventArgsPoolAccept.Pop();
                eventArgs.Dispose();
            }

            while (ClientPool.Count > 0)
            {
                var client = ClientPool.Pop();
                client.Disconnect("Shutdown Server");
            }
        }

        #endregion Disconnect - Shutdown
    }
}
