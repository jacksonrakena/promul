using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Promul.Common.Networking.Data;
using Promul.Common.Networking.Layers;
using Promul.Common.Networking.Utils;

namespace Promul.Common.Networking
{
    public sealed class NetPacketReader : NetDataReader
    {
        private NetPacket? _packet;
        private readonly NetManager _manager;

        internal NetPacketReader(NetManager manager, NetPacket? packet, int headerSize) :
            base(packet == null ? ArraySegment<byte>.Empty : new ArraySegment<byte>(packet.Data.Array, packet.Data.Offset+headerSize, packet.Size))
        {
            _manager = manager;
            _packet = packet;
        }

        internal void RecycleInternal()
        {
            Clear();
            //if (_packet != null) _manager.PoolRecycle(_packet);
            _packet = null;
        }

        public void Recycle()
        {
            if (_manager.AutoRecycle)
                return;
            RecycleInternal();
        }
    }

    internal sealed class NetEvent
    {
        public enum NetEventType
        {
            Connect,
            Disconnect,
            Receive,
            ReceiveUnconnected,
            Error,
            ConnectionLatencyUpdated,
            Broadcast,
            ConnectionRequest,
            MessageDelivered,
            PeerAddressChanged
        }
        public NetEventType Type;

        public NetPeer Peer;
        public IPEndPoint RemoteEndPoint;
        public object UserData;
        public int Latency;
        public SocketError ErrorCode;
        public DisconnectReason DisconnectReason;
        public ConnectionRequest ConnectionRequest;
        public DeliveryMethod DeliveryMethod;
        public byte ChannelNumber;
        public NetPacketReader? DataReader;

        public NetEvent(NetManager manager)
        {
        }
    }

    /// <summary>
    /// Main class for all network operations. Can be used as client and/or server.
    /// </summary>
    public partial class NetManager
    {
#if DEBUG
        private struct IncomingData
        {
            public NetPacket Data;
            public IPEndPoint EndPoint;
            public DateTime TimeWhenGet;
        }
        private readonly List<IncomingData> _pingSimulationList = new List<IncomingData>();
        private readonly Random _randomGenerator = new Random();
        private const int MinLatencyThreshold = 5;
#endif
        
        private readonly Dictionary<IPEndPoint, NetPeer> _peersDict = new Dictionary<IPEndPoint, NetPeer>(new IPEndPointComparer());
        private readonly Dictionary<IPEndPoint, ConnectionRequest> _requestsDict = new Dictionary<IPEndPoint, ConnectionRequest>(new IPEndPointComparer());
        private readonly ConcurrentDictionary<IPEndPoint, NtpRequest> _ntpRequests = new ConcurrentDictionary<IPEndPoint, NtpRequest>(new IPEndPointComparer());
        private volatile NetPeer? _headPeer;
        private int _connectedPeersCount;
        private readonly List<NetPeer> _connectedPeerListCache = new List<NetPeer>();
        private NetPeer[] _peersArray = new NetPeer[32];
        private readonly PacketLayerBase? _extraPacketLayer;
        private int _lastPeerId;
        private ConcurrentQueue<int> _peerIds = new ConcurrentQueue<int>();
        private byte _channelsCount = 1;

        /// <summary>
        /// Enable messages receiving without connection. (with SendUnconnectedMessage method)
        /// </summary>
        public bool UnconnectedMessagesEnabled = false;

        /// <summary>
        /// Enable nat punch messages
        /// </summary>
        public bool NatPunchEnabled = false;

        /// <summary>
        /// Interval for latency detection and checking connection (in milliseconds)
        /// </summary>
        public int PingInterval = 1000;

        /// <summary>
        /// If NetManager doesn't receive any packet from remote peer during this time (in milliseconds) then connection will be closed
        /// (including library internal keepalive packets)
        /// </summary>
        public int DisconnectTimeout = 5000;

        /// <summary>
        /// Simulate packet loss by dropping random amount of packets. (Works only in DEBUG mode)
        /// </summary>
        public bool SimulatePacketLoss = false;

        /// <summary>
        /// Simulate latency by holding packets for random time. (Works only in DEBUG mode)
        /// </summary>
        public bool SimulateLatency = false;

        /// <summary>
        /// Chance of packet loss when simulation enabled. value in percents (1 - 100).
        /// </summary>
        public int SimulationPacketLossChance = 10;

        /// <summary>
        /// Minimum simulated latency (in milliseconds)
        /// </summary>
        public int SimulationMinLatency = 30;

        /// <summary>
        /// Maximum simulated latency (in milliseconds)
        /// </summary>
        public int SimulationMaxLatency = 100;

        /// <summary>
        /// Allows receive broadcast packets
        /// </summary>
        public bool BroadcastReceiveEnabled = false;

        /// <summary>
        /// Delay between initial connection attempts (in milliseconds)
        /// </summary>
        public int ReconnectDelay = 500;

        /// <summary>
        /// Maximum connection attempts before client stops and call disconnect event.
        /// </summary>
        public int MaxConnectAttempts = 10;

        /// <summary>
        /// Enables socket option "ReuseAddress" for specific purposes
        /// </summary>
        public bool ReuseAddress = false;

        /// <summary>
        /// Statistics of all connections
        /// </summary>
        public readonly NetStatistics Statistics = new NetStatistics();

        /// <summary>
        /// Toggles the collection of network statistics for the instance and all known peers
        /// </summary>
        public bool EnableStatistics = false;

        /// <summary>
        /// NatPunchModule for NAT hole punching operations
        /// </summary>
        public readonly NatPunchModule NatPunchModule;

        /// <summary>
        /// Local EndPoint (host and port)
        /// </summary>
        public int LocalPort { get; private set; }

        /// <summary>
        /// Automatically recycle NetPacketReader after OnReceive event
        /// </summary>
        public bool AutoRecycle;

        /// <summary>
        /// IPv6 support
        /// </summary>
        public bool IPv6Enabled = true;

        /// <summary>
        /// Override MTU for all new peers registered in this NetManager, will ignores MTU Discovery!
        /// </summary>
        public int MtuOverride = 0;

        /// <summary>
        /// Sets initial MTU to lowest possible value according to RFC1191 (576 bytes)
        /// </summary>
        public bool UseSafeMtu = false;

        /// <summary>
        /// First peer. Useful for Client mode
        /// </summary>
        public NetPeer? FirstPeer => _headPeer;

        /// <summary>
        /// Disconnect peers if HostUnreachable or NetworkUnreachable spawned (old behaviour 0.9.x was true)
        /// </summary>
        public bool DisconnectOnUnreachable = false;

        /// <summary>
        /// Allows peer change it's ip (lte to wifi, wifi to lte, etc). Use only on server
        /// </summary>
        public bool AllowPeerAddressChange = false;

        /// <summary>
        /// QoS channel count per message type (value must be between 1 and 64 channels)
        /// </summary>
        public byte ChannelsCount
        {
            get => _channelsCount;
            set
            {
                if (value < 1 || value > 64)
                    throw new ArgumentException("Channels count must be between 1 and 64");
                _channelsCount = value;
            }
        }

        /// <summary>
        /// Returns connected peers list (with internal cached list)
        /// </summary>
        public List<NetPeer> ConnectedPeerList
        {
            get
            {
                CopyPeersIntoList(_connectedPeerListCache, ConnectionState.Connected);
                return _connectedPeerListCache;
            }
        }

        /// <summary>
        /// Gets a peer by ID.
        /// </summary>
        public NetPeer? GetPeerById(int id)
        {
            if (id >= 0 && id < _peersArray.Length)
            {
                return _peersArray[id];
            }

            return null;
        }

        /// <summary>
        /// Gets a peer by ID.
        /// </summary>
        public bool TryGetPeerById(int id, out NetPeer peer)
        {
            var tmp = GetPeerById(id);
            peer = tmp!;

            return tmp != null;
        }

        public int ExtraPacketSizeForLayer => _extraPacketLayer?.ExtraPacketSizeForLayer ?? 0;

        private bool TryGetPeer(IPEndPoint endPoint, out NetPeer peer)
        {
            bool result = _peersDict.TryGetValue(endPoint, out peer);
            return result;
        }

        private void AddPeer(NetPeer peer)
        {
            if (_headPeer != null)
            {
                peer.NextPeer = _headPeer;
                _headPeer.PrevPeer = peer;
            }
            _headPeer = peer;
            _peersDict.Add(peer.EndPoint, peer);
            if (peer.Id >= _peersArray.Length)
            {
                int newSize = _peersArray.Length * 2;
                while (peer.Id >= newSize)
                    newSize *= 2;
                Array.Resize(ref _peersArray, newSize);
            }
            _peersArray[peer.Id] = peer;
        }

        private void RemovePeer(NetPeer peer)
        {
            RemovePeerInternal(peer);
        }

        private void RemovePeerInternal(NetPeer peer)
        {
            if (!_peersDict.Remove(peer.EndPoint))
                return;
            if (peer == _headPeer)
                _headPeer = peer.NextPeer;

            if (peer.PrevPeer != null)
                peer.PrevPeer.NextPeer = peer.NextPeer;
            if (peer.NextPeer != null)
                peer.NextPeer.PrevPeer = peer.PrevPeer;
            peer.PrevPeer = null;

            _peersArray[peer.Id] = null;
            _peerIds.Enqueue(peer.Id);
        }

        /// <summary>
        /// Creates a new <see cref="NetManager"/>.
        /// </summary>
        /// <param name="listener">Network events listener (also can implement IDeliveryEventListener)</param>
        /// <param name="extraPacketLayer">Extra processing of packages, like CRC checksum or encryption. All connected NetManagers must have same layer.</param>
        public NetManager(PacketLayerBase? extraPacketLayer = null)
        {
            NatPunchModule = new NatPunchModule(this);
            _extraPacketLayer = extraPacketLayer;
        }

        internal async Task ConnectionLatencyUpdated(NetPeer fromPeer, int latency)
        {
            if (OnNetworkLatencyUpdate != null) await OnNetworkLatencyUpdate(fromPeer, latency);
        }

        internal async Task MessageDelivered(NetPeer fromPeer, object? userData)
        {
            if (OnMessageDelivered != null) await OnMessageDelivered(fromPeer, userData);
        }
        

        [Conditional("DEBUG")]
        private void ProcessDelayedPackets()
        {
#if DEBUG
            if (!SimulateLatency)
                return;

            var time = DateTime.UtcNow;
            lock (_pingSimulationList)
            {
                for (int i = 0; i < _pingSimulationList.Count; i++)
                {
                    var incomingData = _pingSimulationList[i];
                    if (incomingData.TimeWhenGet <= time)
                    {
                        DebugMessageReceived(incomingData.Data, incomingData.EndPoint);
                        _pingSimulationList.RemoveAt(i);
                        i--;
                    }
                }
            }
#endif
        }

        private void ProcessNtpRequests(long elapsedMilliseconds)
        {
            List<IPEndPoint> requestsToRemove = null;
            foreach (var ntpRequest in _ntpRequests)
            {
                ntpRequest.Value.Send(_udpSocketv4, elapsedMilliseconds);
                if(ntpRequest.Value.NeedToKill)
                {
                    if (requestsToRemove == null)
                        requestsToRemove = new List<IPEndPoint>();
                    requestsToRemove.Add(ntpRequest.Key);
                }
            }

            if (requestsToRemove != null)
            {
                foreach (var ipEndPoint in requestsToRemove)
                {
                    _ntpRequests.TryRemove(ipEndPoint, out _);
                }
            }
        }

        internal async Task<NetPeer> OnConnectionRequestResolved(ConnectionRequest request, ArraySegment<byte> data)
        {
            NetPeer netPeer = null;

            if (request.Result == ConnectionRequestResult.RejectForce)
            {
                NetDebug.Write(NetLogLevel.Trace, "[NM] Peer connect reject force.");
                if (data is { Array: not null, Count: > 0 })
                {
                    var shutdownPacket = NetPacket.FromProperty(PacketProperty.Disconnect, data.Count);
                    shutdownPacket.ConnectionNumber = request.InternalPacket.ConnectionNumber;
                    FastBitConverter.GetBytes(shutdownPacket.Data.Array, shutdownPacket.Data.Offset+1, request.InternalPacket.ConnectionTime);
                    if (shutdownPacket.Size >= NetConstants.PossibleMtu[0])
                        NetDebug.WriteError("[Peer] Disconnect additional data size more than MTU!");
                    else data.CopyTo(shutdownPacket.Data.Array, shutdownPacket.Data.Offset+9);
                    await SendRawAndRecycle(shutdownPacket, request.RemoteEndPoint);
                }
            }
            else
            {
                if (_peersDict.TryGetValue(request.RemoteEndPoint, out netPeer))
                {
                    //already have peer
                }
                else if (request.Result == ConnectionRequestResult.Reject)
                {
                    netPeer = new NetPeer(this, request.RemoteEndPoint, GetNextPeerId());
                    await netPeer.RejectAsync(request.InternalPacket, data);
                    AddPeer(netPeer);
                    NetDebug.Write(NetLogLevel.Trace, "[NM] Peer connect reject.");
                }
                else //Accept
                {
                    netPeer = await NetPeer.AcceptAsync(this, request, GetNextPeerId());
                    AddPeer(netPeer);
                    if (OnPeerConnected != null) await OnPeerConnected(netPeer);
                    NetDebug.Write(NetLogLevel.Trace, $"[NM] Received peer connection Id: {netPeer.ConnectTime}, EP: {netPeer.EndPoint}");
                }
            }

            _requestsDict.Remove(request.RemoteEndPoint);

            return netPeer;
        }

        private int GetNextPeerId()
        {
            return _peerIds.TryDequeue(out int id) ? id : _lastPeerId++;
        }

        private async Task ProcessConnectRequest(
            IPEndPoint remoteEndPoint,
            NetPeer netPeer,
            NetConnectRequestPacket connRequest)
        {
            //if we have peer
            if (netPeer != null)
            {
                var processResult = await netPeer.ProcessConnectRequest(connRequest);
                NetDebug.Write($"ConnectRequest LastId: {netPeer.ConnectTime}, NewId: {connRequest.ConnectionTime}, EP: {remoteEndPoint}, Result: {processResult}");

                switch (processResult)
                {
                    case ConnectRequestResult.Reconnection:
                        await ForceDisconnectPeerAsync(netPeer, DisconnectReason.Reconnect, 0, null);
                        RemovePeer(netPeer);
                        //go to new connection
                        break;
                    case ConnectRequestResult.NewConnection:
                        RemovePeer(netPeer);
                        //go to new connection
                        break;
                    case ConnectRequestResult.P2PLose:
                        await ForceDisconnectPeerAsync(netPeer, DisconnectReason.PeerToPeerConnection, 0, null);
                        RemovePeer(netPeer);
                        //go to new connection
                        break;
                    default:
                        //no operations needed
                        return;
                }
                //ConnectRequestResult.NewConnection
                //Set next connection number
                if(processResult != ConnectRequestResult.P2PLose)
                    connRequest.ConnectionNumber = (byte)((netPeer.ConnectionNum + 1) % NetConstants.MaxConnectionNumber);
                //To reconnect peer
            }
            else
            {
                NetDebug.Write($"ConnectRequest Id: {connRequest.ConnectionTime}, EP: {remoteEndPoint}");
            }

            ConnectionRequest req;
            lock (_requestsDict)
            {
                if (_requestsDict.TryGetValue(remoteEndPoint, out req))
                {
                    req.UpdateRequest(connRequest);
                    return;
                }
                req = new ConnectionRequest(remoteEndPoint, connRequest, this);
                _requestsDict.Add(remoteEndPoint, req);
            }
            NetDebug.Write($"[NM] Creating request event: {connRequest.ConnectionTime}");
            if (OnConnectionRequest != null) await OnConnectionRequest(req);
        }

        private async Task OnMessageReceived(NetPacket packet, IPEndPoint remoteEndPoint)
        {
#if DEBUG
            if (SimulatePacketLoss && _randomGenerator.NextDouble() * 100 < SimulationPacketLossChance)
            {
                //drop packet
                return;
            }
            if (SimulateLatency)
            {
                int latency = _randomGenerator.Next(SimulationMinLatency, SimulationMaxLatency);
                if (latency > MinLatencyThreshold)
                {
                    _pingSimulationList.Add(new IncomingData
                    {
                        Data = packet,
                        EndPoint = remoteEndPoint,
                        TimeWhenGet = DateTime.UtcNow.AddMilliseconds(latency)
                    });
                    
                    return;
                }
            }

            await DebugMessageReceived(packet, remoteEndPoint);
        }

        private async Task DebugMessageReceived(NetPacket packet, IPEndPoint remoteEndPoint)
        {
#endif
            var originalPacketSize = packet.Size;
            if (EnableStatistics)
            {
                Statistics.IncrementPacketsReceived();
                Statistics.AddBytesReceived(originalPacketSize);
            }

            if (_ntpRequests.Count > 0)
            {
                if (_ntpRequests.TryGetValue(remoteEndPoint, out var request))
                {
                    if (packet.Size < 48)
                    {
                        NetDebug.Write(NetLogLevel.Trace, $"NTP response too short: {packet.Size}");
                        return;
                    }

                    var copiedData = packet.Data.ToArray();
                    NtpPacket ntpPacket = NtpPacket.FromServerResponse(copiedData, DateTime.UtcNow);
                    try
                    {
                        ntpPacket.ValidateReply();
                    }
                    catch (InvalidOperationException ex)
                    {
                        NetDebug.Write(NetLogLevel.Trace, $"NTP response error: {ex.Message}");
                        ntpPacket = null;
                    }

                    if (ntpPacket != null)
                    {
                        _ntpRequests.TryRemove(remoteEndPoint, out _);
                        if (OnNtpResponse != null) await OnNtpResponse(ntpPacket);
                    }
                    return;
                }
            }

            if (_extraPacketLayer != null)
            {
                int start = 0;
                _extraPacketLayer.ProcessInboundPacket(ref remoteEndPoint, ref packet);
                if (packet.Size == 0)
                    return;
            }

            if (!packet.Verify())
            {
                NetDebug.WriteError("[NM] DataReceived: bad!");
                //PoolRecycle(packet);
                return;
            }

            switch (packet.Property)
            {
                //special case connect request
                case PacketProperty.ConnectRequest:
                    if (NetConnectRequestPacket.GetProtocolId(packet) != NetConstants.ProtocolId)
                    {
                        await SendRawAndRecycle(NetPacket.FromProperty(PacketProperty.InvalidProtocol, 0), remoteEndPoint);
                        return;
                    }
                    break;
                //unconnected messages
                case PacketProperty.Broadcast:
                    if (!BroadcastReceiveEnabled)
                        return;
                    await OnConnectionlessReceive(remoteEndPoint, new NetPacketReader(this, packet, packet.GetHeaderSize()), UnconnectedMessageType.Broadcast);
                    return;
                case PacketProperty.UnconnectedMessage:
                    if (!UnconnectedMessagesEnabled)
                        return;
                    
                    await OnConnectionlessReceive(remoteEndPoint, new NetPacketReader(this, packet, packet.GetHeaderSize()), UnconnectedMessageType.BasicMessage);
                    return;
                case PacketProperty.NatMessage:
                    if (NatPunchEnabled)
                        NatPunchModule.ProcessMessage(remoteEndPoint, packet);
                    return;
            }

            //Check normal packets
            bool peerFound = _peersDict.TryGetValue(remoteEndPoint, out var netPeer);

            if (peerFound && EnableStatistics)
            {
                netPeer.Statistics.IncrementPacketsReceived();
                netPeer.Statistics.AddBytesReceived(originalPacketSize);
            }
            
            switch (packet.Property)
            {
                case PacketProperty.ConnectRequest:
                    var connRequest = NetConnectRequestPacket.FromData(packet);
                    if (connRequest != null)
                        await ProcessConnectRequest(remoteEndPoint, netPeer, connRequest);
                    break;
                case PacketProperty.PeerNotFound:
                    if (peerFound) //local
                    {
                        if (netPeer.ConnectionState != ConnectionState.Connected)
                            return;
                        if (packet.Size == 1)
                        {
                            //first reply
                            //send NetworkChanged packet
                            netPeer.ResetMtu();
                            await SendRaw(NetConnectAcceptPacket.MakeNetworkChanged(netPeer), remoteEndPoint);
                            NetDebug.Write($"PeerNotFound sending connection info: {remoteEndPoint}");
                        }
                        else if (packet.Size == 2 && packet.Data.Array[packet.Data.Offset+1] == 1)
                        {
                            //second reply
                            await ForceDisconnectPeerAsync(netPeer, DisconnectReason.PeerNotFound, 0, null);
                        }
                    }
                    else if (packet.Size > 1) //remote
                    {
                        //check if this is old peer
                        bool isOldPeer = false;

                        if (AllowPeerAddressChange)
                        {
                            NetDebug.Write($"[NM] Looks like address change: {packet.Size}");
                            var remoteData = NetConnectAcceptPacket.FromData(packet);
                            if (remoteData != null &&
                                remoteData.PeerNetworkChanged &&
                                remoteData.PeerId < _peersArray.Length)
                            {
                                var peer = _peersArray[remoteData.PeerId];
                                if (peer != null &&
                                    peer.ConnectTime == remoteData.ConnectionTime &&
                                    peer.ConnectionNum == remoteData.ConnectionNumber)
                                {
                                    if (peer.ConnectionState == ConnectionState.Connected)
                                    {
                                        peer.InitiateEndPointChange();
                                        if (OnPeerAddressChanged != null) await OnPeerAddressChanged(peer, remoteEndPoint);
                                        NetDebug.Write("[NM] PeerNotFound change address of remote peer");
                                    }
                                    isOldPeer = true;
                                }
                            }
                        }

                        //PoolRecycle(packet);

                        //else peer really not found
                        if (!isOldPeer)
                        {
                            var secondResponse = NetPacket.FromProperty(PacketProperty.PeerNotFound, 1);
                            secondResponse.Data.Array[secondResponse.Data.Offset+1] = 1;
                            await SendRawAndRecycle(secondResponse, remoteEndPoint);
                        }
                    }
                    break;
                case PacketProperty.InvalidProtocol:
                    if (peerFound && netPeer.ConnectionState == ConnectionState.Outgoing)
                        await ForceDisconnectPeerAsync(netPeer, DisconnectReason.InvalidProtocol, 0, null);
                    break;
                case PacketProperty.Disconnect:
                    if (peerFound)
                    {
                        var disconnectResult = netPeer.ProcessDisconnect(packet);
                        if (disconnectResult == DisconnectResult.None)
                        {
                            //PoolRecycle(packet);
                            return;
                        }
                        await ForceDisconnectPeerAsync(
                            netPeer,
                            disconnectResult == DisconnectResult.Disconnect
                            ? DisconnectReason.RemoteConnectionClose
                            : DisconnectReason.ConnectionRejected,
                            0, packet);
                    }
                    else
                    {
                        //PoolRecycle(packet);
                    }
                    //Send shutdown
                    await SendRawAndRecycle(NetPacket.FromProperty(PacketProperty.ShutdownOk, 0), remoteEndPoint);
                    break;
                case PacketProperty.ConnectAccept:
                    if (!peerFound)
                        return;
                    var connAccept = NetConnectAcceptPacket.FromData(packet);
                    if (connAccept != null && netPeer.ProcessConnectAccept(connAccept))
                    {
                        if (OnPeerConnected != null) await OnPeerConnected(netPeer);
                    }
                    break;
                default:
                    if(peerFound)
                        netPeer.ProcessPacket(packet);
                    else
                        await SendRawAndRecycle(NetPacket.FromProperty(PacketProperty.PeerNotFound, 0), remoteEndPoint);
                    break;
            }
        }

        internal async Task CreateReceiveEvent(NetPacket packet, DeliveryMethod method, byte channelNumber, int headerSize, NetPeer fromPeer)
        {
            if (OnReceive != null) await OnReceive(fromPeer, new NetPacketReader(this, packet, headerSize), channelNumber, method);
        }
        
        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(ArraySegment<byte> data, DeliveryMethod options = global::Promul.Common.Networking.DeliveryMethod.ReliableOrdered, byte channelNumber = 0, NetPeer? excludePeer = null)
        {
            try
            {
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                {
                    if (netPeer != excludePeer)
                        netPeer.Send(data, options, channelNumber);
                }
            }
            finally
            {
            }
        }


        /// <summary>
        /// Send a connectionless message.
        /// </summary>
        /// <param name="data">The data to send.</param>
        /// <param name="remoteEndPoint">The destination.</param>
        /// <returns>Whether the send operation was successful.</returns>
        public async Task<bool> SendConnectionlessMessageAsync(ArraySegment<byte> data, IPEndPoint remoteEndPoint)
        {
            //No need for CRC here, SendRaw does that
            var packet = NetPacket.FromBuffer(data);
            packet.Property = PacketProperty.UnconnectedMessage;
            return await SendRawAndRecycle(packet, remoteEndPoint) > 0;
        }

        /// <summary>
        ///     Connects to a remote host by IP address.
        /// </summary>
        /// <param name="target">The endpoint of the remote host.</param>
        /// <param name="connectionData">Additional data presented to the remote host.</param>
        /// <returns>The NetPeer, if connection was successful. Returns null if we are waiting for a response.</returns>
        public async Task<NetPeer?> ConnectAsync(IPEndPoint target, ArraySegment<byte> connectionData)
        {
            if (_requestsDict.ContainsKey(target))
                return null;

            byte connectionNumber = 0;
            if (_peersDict.TryGetValue(target, out var peer))
            {
                switch (peer.ConnectionState)
                {
                    //just return already connected peer
                    case ConnectionState.Connected:
                    case ConnectionState.Outgoing:
                        return peer;
                }
                //else reconnect
                connectionNumber = (byte)((peer.ConnectionNum + 1) % NetConstants.MaxConnectionNumber);
                RemovePeer(peer);
            }

            //Create reliable connection
            //And send connection request
            peer = await NetPeer.ConnectToAsync(this, target, GetNextPeerId(), connectionNumber, connectionData);
            AddPeer(peer);

            return peer;
        }

        /// <summary>
        /// Forces all peers to disconnect.
        /// </summary>
        /// <param name="sendDisconnectMessages">Whether to notify peers of pending disconnection.</param>
        public async Task StopAsync(bool sendDisconnectMessages = true)
        {
            NetDebug.Write("[NM] Stop");

#if UNITY_2018_3_OR_NEWER
            _pausedSocketFix.Deinitialize();
            _pausedSocketFix = null;
#endif

            //Send last disconnect
            for(var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                await netPeer.ShutdownAsync(null, !sendDisconnectMessages);

            //Stop
            CloseSocket();
            

            //clear peers
//             _peersLock.EnterWriteLock();
             _headPeer = null;
//             _peersDict.Clear();
             _peersArray = new NetPeer[32];
//             _peersLock.ExitWriteLock();
            _peerIds = new ConcurrentQueue<int>();
            _lastPeerId = 0;
#if DEBUG
            lock (_pingSimulationList)
                _pingSimulationList.Clear();
#endif
            _connectedPeersCount = 0;
        }

        /// <summary>
        /// Returns the number of peers in a given connection state.
        /// </summary>
        /// <param name="peerState">The state to query. Bit flags are supported.</param>
        /// <returns>The number of peers who are in the given state.</returns>
        public int GetNumberOfPeersInState(ConnectionState peerState)
        {
            int count = 0;
            for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
            {
                if ((netPeer.ConnectionState & peerState) != 0)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Copies all peers in a given state into the supplied list. This method avoids allocations.
        /// </summary>
        /// <param name="peers">This list will be cleared and populated with the output of the query.</param>
        /// <param name="peerState">The state to query. Bit flags are supported.</param>
        public void CopyPeersIntoList(List<NetPeer> peers, ConnectionState peerState)
        {
            peers.Clear();
            for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
            {
                if ((netPeer.ConnectionState & peerState) != 0)
                    peers.Add(netPeer);
            }
        }

        /// <summary>
        /// Gracefully disconnects all peers.
        /// </summary>
        /// <param name="data">The shutdown message to be sent to each peer.
        /// As only one message is sent, the size of this data must be less than or equal to the current MTU.</param>
        public async Task DisconnectAllPeersAsync(ArraySegment<byte> data = default)
        {
            //Send disconnect packets
            for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
            {
                await DisconnectPeerAsync(netPeer, data);
            }
        }

        /// <summary>
        /// Gracefully disconnects a given peer.
        /// </summary>
        /// <param name="peer">The peer to disconnect.</param>
        /// <param name="data">The shutdown message to be sent to each peer.
        /// As only one message is sent, the size of this data must be less than or equal to the current MTU.</param>
        public Task DisconnectPeerAsync(NetPeer peer, ArraySegment<byte> data = default)
        {
            return DisconnectPeerAsync(
                peer,
                DisconnectReason.DisconnectPeerCalled,
                0,
                false,
                data,
                null);
        }
        
        /// <summary>
        /// Immediately disconnects a given peer without providing them with additional data.
        /// </summary>
        /// <param name="peer">The peer to disconnect.</param>
        public Task ForceDisconnectPeerAsync(NetPeer peer, DisconnectReason reason = DisconnectReason.DisconnectPeerCalled, SocketError errorCode = 0, NetPacket? data = null)
        {
            return DisconnectPeerAsync(peer, reason, errorCode, true, null, data);
        }
        
        async Task DisconnectPeerAsync(
            NetPeer peer,
            DisconnectReason reason,
            SocketError socketErrorCode,
            bool force,
            ArraySegment<byte> data,
            NetPacket? eventData)
        {
            var shutdownResult = await peer.ShutdownAsync(data, force);
            switch (shutdownResult)
            {
                case ShutdownResult.None:
                    return;
                case ShutdownResult.WasConnected:
                    Interlocked.Decrement(ref _connectedPeersCount);
                    break;
                case ShutdownResult.Success:
                default:
                    break;
            }
            if (OnPeerDisconnected != null) await OnPeerDisconnected(peer, new DisconnectInfo { Reason = reason, 
                SocketErrorCode = socketErrorCode,
                AdditionalData = new NetPacketReader(this, eventData, eventData?.GetHeaderSize() ?? 0) });
        }
        

        /// <summary>
        /// Create the requests for NTP server
        /// </summary>
        /// <param name="endPoint">NTP Server address.</param>
        public void CreateNtpRequest(IPEndPoint endPoint)
        {
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }

        /// <summary>
        /// Create the requests for NTP server
        /// </summary>
        /// <param name="ntpServerAddress">NTP Server address.</param>
        /// <param name="port">port</param>
        public void CreateNtpRequest(string ntpServerAddress, int port)
        {
            IPEndPoint endPoint = NetUtils.MakeEndPoint(ntpServerAddress, port);
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }

        /// <summary>
        /// Create the requests for NTP server (default port)
        /// </summary>
        /// <param name="ntpServerAddress">NTP Server address.</param>
        public void CreateNtpRequest(string ntpServerAddress)
        {
            IPEndPoint endPoint = NetUtils.MakeEndPoint(ntpServerAddress, NtpRequest.DefaultPort);
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }
    }
}
