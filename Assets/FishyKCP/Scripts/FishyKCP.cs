using FishNet.Managing;
using FishNet.Transporting;
using kcp2k;
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace Trongtindev.FishyKCP
{
    public class FishyKCP : Transport
    {
        #region Types
        struct RemoteConnectionEvent
        {
            public readonly bool Connected;
            public readonly int ConnectionId;
            public RemoteConnectionEvent(bool connected, int connectionId)
            {
                Connected = connected;
                ConnectionId = connectionId;
            }
        }
        #endregion

        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;
        public override event Action<ClientReceivedDataArgs> OnClientReceivedData;
        public override event Action<ServerReceivedDataArgs> OnServerReceivedData;

        #region Fields
        [Tooltip("Port to use.")]
        [SerializeField] private ushort _port = 7777;

        [Tooltip("Address to connect.")]
        [SerializeField]
        private string _clientAddress = "localhost";

        [Tooltip("DualMode uses both IPv6 and IPv4. not all platforms support it.")]
        [SerializeField] private bool _dualMode = false;

        [Tooltip("configurable MTU in case kcp sits on top of other abstractions like")]
        [Range(576, Kcp.MTU_DEF)]
        [SerializeField] private int _mtu = Kcp.MTU_DEF;

        [Tooltip("NoDelay is recommended to reduce latency. This also scales better")]
        [SerializeField] private bool _noDelay = true;

        [Tooltip("KCP internal update interval. 100ms is KCP default, but a lower interval is recommended to minimize latency and to scale to more networked entities.")]
        [SerializeField] private uint _interval = 10;

        [Tooltip("KCP fastresend parameter. Faster resend for the cost of higher bandwidth.")]
        [SerializeField] private int _fastResend = 0;

        [Tooltip("KCP congestion window heavily limits messages flushed per update. best to leave this disabled, as it may significantly increase latency.")]
        [SerializeField] private bool _congestionWindow = false;

        [Tooltip("Timeout in milliseconds")]
        [SerializeField] private int _timeout = KcpPeer.DEFAULT_TIMEOUT;

        [Tooltip("Maximum retransmission attempts until dead_link")]
        [SerializeField] private uint _maxRetransmits = Kcp.DEADLINK;

        private KcpConfig _kcpConfig;
        private KcpServer _kcpServer;
        private LocalConnectionState _serverState;
        private KcpClient _kcpClient;
        private LocalConnectionState _clientState;

        /// <summary>
        /// ConnectionEvents which need to be handled.
        /// </summary>
        private readonly ConcurrentQueue<RemoteConnectionEvent> _remoteConnectionEvents = new();
        #endregion

        #region Properties
        protected LocalConnectionState ServerState
        {
            get => _serverState;
            set
            {
                base.NetworkManager.Log($"[KCP] ServerState({value})");
                _serverState = value;
                HandleServerConnectionState(new ServerConnectionStateArgs(value, 0));
            }
        }

        protected LocalConnectionState ClientState
        {
            get => _clientState;
            set
            {
                base.NetworkManager.Log($"[KCP] ClientState({value})");
                _clientState = value;
                HandleClientConnectionState(new ClientConnectionStateArgs(value, 0));
            }
        }
        #endregion

        #region Lifecycle
        private void OnApplicationQuit() => Shutdown();

        public override void Initialize(NetworkManager networkManager, int transportIndex)
        {
            base.Initialize(networkManager, transportIndex);

            _kcpConfig = new KcpConfig()
            {
                DualMode = _dualMode,
                Mtu = _mtu,
                NoDelay = _noDelay,
                Interval = _interval,
                FastResend = _fastResend,
                Timeout = _timeout,
                MaxRetransmits = _maxRetransmits,
            };
        }

        public override void Shutdown()
        {
            if (_kcpServer != default) StopConnection(true);
            if (_kcpClient != default) StopConnection(true);
        }
        #endregion

        #region Methods
        public override int GetMTU(byte channel)
        {
            return _mtu;
        }

        private bool TryGetClient(int connectionId, out KcpServerConnection connection)
        {
            connection = default;
            if (_kcpServer.connections.ContainsKey(connectionId)) connection = _kcpServer.connections[connectionId];
            return connection != default;
        }

        public override string GetConnectionAddress(int connectionId)
        {
            return TryGetClient(connectionId, out var connection) ? connection.remoteEndPoint.ToString() : string.Empty;
        }
        #endregion

        #region Local Events
        private void OnServerConnected(int connectionId)
        {
            base.NetworkManager.Log($"[KCP] OnServerConnected({connectionId})");
            _remoteConnectionEvents.Enqueue(new(true, connectionId));
        }

        private void OnServerDataReceived(int connectionId, ArraySegment<byte> message, KcpChannel channel)
        {
            //Handle connection and disconnection events.
            while (_remoteConnectionEvents.TryDequeue(out RemoteConnectionEvent connectionEvent))
            {
                RemoteConnectionState state = connectionEvent.Connected ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
                HandleRemoteConnectionState(new(state, connectionEvent.ConnectionId, 0));
            }

            HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message, (Channel)(channel - 1), connectionId, 0));
        }

        private void OnServerDisconnected(int connectionId)
        {
            base.NetworkManager.Log($"[KCP] OnServerDisconnected({connectionId})");
            _remoteConnectionEvents.Enqueue(new(false, connectionId));
        }

        private void OnServerError(int connectionId, ErrorCode error, string reason)
        {
            base.NetworkManager.LogError($"[KCP] OnServerError({connectionId},{error},{reason})");
        }

        private void OnClientConnected()
        {
            ClientState = LocalConnectionState.Started;
        }

        private void OnClientDataReceived(ArraySegment<byte> message, KcpChannel channel)
        {
            HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message, (Channel)(channel - 1), 0));
        }

        private void OnClientDisconnected()
        {
            //ClientState = LocalConnectionState.Stopped;
        }

        private void OnClientError(ErrorCode error, string reason)
        {
            base.NetworkManager.LogError($"[KCP] OnClientError({error},{reason})");
            ClientState = LocalConnectionState.StoppedError;
        }
        #endregion

        public override LocalConnectionState GetConnectionState(bool server)
        {
            return server ? ServerState : ClientState;
        }

        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            return _kcpServer.connections.ContainsKey(connectionId) ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
        }

        public override void HandleClientConnectionState(ClientConnectionStateArgs connectionStateArgs)
        {
            OnClientConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs receivedDataArgs)
        {
            OnClientReceivedData?.Invoke(receivedDataArgs);
        }

        public override void HandleRemoteConnectionState(RemoteConnectionStateArgs connectionStateArgs)
        {
            OnRemoteConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleServerConnectionState(ServerConnectionStateArgs connectionStateArgs)
        {
            OnServerConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs receivedDataArgs)
        {
            OnServerReceivedData?.Invoke(receivedDataArgs);
        }

        public override void IterateIncoming(bool server)
        {
            if (server && _kcpServer != default)
            {
                _kcpServer.TickIncoming();
            }
            else if (_kcpClient != default)
            {
                _kcpClient.TickIncoming();
            }
        }

        public override void IterateOutgoing(bool server)
        {
            if (server && _kcpServer != default)
            {
                _kcpServer.TickOutgoing();
            }
            else if (_kcpClient != default)
            {
                _kcpClient.TickOutgoing();
            }
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            _kcpServer.Send(connectionId, segment, (KcpChannel)(channelId + 1));
        }

        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            _kcpClient.Send(segment, (KcpChannel)(channelId + 1));
        }

        public override bool StartConnection(bool server)
        {
            base.NetworkManager.Log($"[KCP] StartConnection({server})");

            if (server)
            {
                ServerState = LocalConnectionState.Starting;
                _kcpServer = new KcpServer(OnServerConnected, OnServerDataReceived, OnServerDisconnected, OnServerError, _kcpConfig);
                _kcpServer.Start(_port);
                ServerState = LocalConnectionState.Started;

                return true;
            }

            ClientState = LocalConnectionState.Starting;
            _kcpClient = new KcpClient(OnClientConnected, OnClientDataReceived, OnClientDisconnected, OnClientError, _kcpConfig);
            _kcpClient.Connect(_clientAddress, _port);

            return true;
        }

        public override bool StopConnection(bool server)
        {
            if (_kcpServer != default)
            {
                ServerState = LocalConnectionState.Stopping;
                _kcpServer.Stop();
                ServerState = LocalConnectionState.Stopped;
                _kcpServer = default;
                while (_remoteConnectionEvents.TryDequeue(out _)) { }
            }

            if (_kcpClient != default)
            {
                ClientState = LocalConnectionState.Stopping;
                _kcpClient.Disconnect();
                _kcpClient = default;
            }

            return true;
        }

        public override bool StopConnection(int connectionId, bool immediately)
        {
            if (_kcpServer != default)
            {
                _kcpServer.Disconnect(connectionId);
                return true;
            }
            return false;
        }
    }
}
