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
using Promul.Common.Networking.Packets;
using Promul.Common.Networking.Packets.Internal;

namespace Promul.Common.Networking
{
    /// <summary>
    ///     This class represents the entry-point to Promul Networking communications.
    ///     It is responsible for creating, managing, and destroying peers, and managing the socket.
    /// </summary>
    public partial class PromulManager
    {
#if DEBUG
        private struct IncomingData
        {
            public NetworkPacket Data;
            public IPEndPoint EndPoint;
            public DateTime TimeWhenGet;
        }
        private readonly List<IncomingData> _pingSimulationList = new List<IncomingData>();
        private readonly Random _randomGenerator = new Random();
        private const int MinLatencyThreshold = 5;
#endif
        
        private readonly Dictionary<IPEndPoint, PromulPeer> _peers = new(new IPEndPointComparer());
        private readonly Dictionary<IPEndPoint, ConnectionRequest> _connectionRequests = new(new IPEndPointComparer());
        private readonly ConcurrentDictionary<IPEndPoint, NtpRequest> _ntpRequests = new(new IPEndPointComparer());
        private volatile PromulPeer? _headPeer;
        private int _connectedPeersCount;
        private readonly List<PromulPeer> _connectedPeerListCache = new();
        private PromulPeer[] _peersArray = new PromulPeer[32];
        private readonly PacketLayerBase? _extraPacketLayer;
        private int _lastPeerId;
        private ConcurrentQueue<int> _peerIds = new();
        private byte _channelsCount = 1;

        /// <summary>
        ///     Allow messages to be received from peers that the manager has not established a connection with.
        ///     To send a connectionless message, call <see cref="SendConnectionlessMessageAsync"/>.
        /// </summary>
        public bool ConnectionlessMessagesAllowed { get; set; } = false;

        /// <summary>
        ///     The interval, in milliseconds, at which to send ping messages to active peers.
        /// </summary>
        public int PingInterval { get; set; } = 1000;

        /// <summary>
        ///     The maximum time, in milliseconds, that the manager will allow before disconnecting a peer for inactivity.
        ///     All clients must have a <see cref="PingInterval"/> lower than this value to avoid unintentional timeouts.
        /// </summary>
        public int DisconnectTimeout { get; set; } = 5000;

        /// <summary>
        ///     Whether to enable the packet loss simulator.
        ///     This setting will not do anything if the DEBUG constant is not defined.
        /// </summary>
        public bool SimulatePacketLoss { get; set; } = false;

        /// <summary>
        ///     Whether to simulate latency by holding packets for a random period of time before sending.
        ///     This setting will not do anything if the DEBUG constant is not defined.
        /// </summary>
        public bool SimulateLatency { get; set; } = false;

        /// <summary>
        ///     The chance (percentage between 0 and 100) of packet loss.
        ///     Requires <see cref="SimulatePacketLoss"/> to be enabled.
        /// </summary>
        public int SimulatePacketLossChance { get; set; } = 10;

        /// <summary>
        ///     The minimum latency, in milliseconds, to add when <see cref="SimulateLatency"/> is enabled.
        /// </summary>
        public int SimulationMinLatency { get; set; } = 30;

        /// <summary>
        ///     The maximum latency, in milliseconds, to add when <see cref="SimulateLatency"/> is enabled.
        /// </summary>
        public int SimulationMaxLatency { get; set; } = 100;

        /// <summary>
        ///     Whether to receive broadcast messages. When this is disabled, the manager
        ///     will ignore all broadcast messages.
        /// </summary>
        public bool BroadcastMessagesAllowed { get; set; } = false;

        /// <summary>
        ///     The delay, in milliseconds, to wait before attempting a reconnect.
        /// </summary>
        public int ReconnectDelay { get; set; } = 500;

        /// <summary>
        ///     The maximum connection attempts the manger will make before stopping and calling <see cref="OnPeerDisconnected"/>.
        /// </summary>
        public int MaximumConnectionAttempts { get; set; } = 10;

        /// <summary>
        /// TODO UPDATE
        /// Enables socket option "ReuseAddress" for specific purposes
        /// </summary>
        public bool ReuseAddress { get; set; } = false;

        /// <summary>
        ///     The recorded statistics. This value will be empty unless <see cref="RecordNetworkStatistics"/> is enabled.
        /// </summary>
        public NetStatistics Statistics { get; } = new NetStatistics();

        /// <summary>
        ///     Whether to record network statistics for the manager and all known peers.
        ///     If this value is true, <see cref="Statistics"/> will be updated with statistics information.
        /// </summary>
        public bool RecordNetworkStatistics { get; set; } = false;

        /// <summary>
        ///     The local port that the scoket is running on.
        /// </summary>
        public int LocalPort { get; private set; }

        /// <summary>
        ///     Whether to listen and send packets over Internet Protocol version 6 (IPv6).
        /// </summary>
        public bool Ipv6Enabled { get; set; } = true;

        /// <summary>
        ///     If set, this value will override the MTU value for all new peers after it has been set.
        ///     This will ignore the MTU discovery process.
        /// </summary>
        public int MtuOverride { get; set; } = 0;

        /// <summary>
        ///     Sets the initial MTU to the lowest possible value according to RFC-1191 (Path MTU Discovery),
        ///     that is, 576 bytes.
        /// </summary>
        public bool UseSafeMtu { get; set; } = false;
        
        /// <summary>
        ///     Whether to disconnect peers if the host or network is unreachable.
        /// </summary>
        public bool DisconnectOnUnreachable { get; set; } = false;

        /// <summary>
        ///     Allows a peer to change its remote endpoint. This occurs, for example,
        ///     when a device moves from an LTE connection to Wi-Fi, or vice versa.
        ///     This setting has safety implications.
        /// </summary>
        public bool AllowPeerAddressChange { get; set; }= false;

        /// <summary>
        ///     The first peer that this manager has connected to.
        /// </summary>
        public PromulPeer? FirstPeer => _headPeer;

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
        ///     The list of all currently connected peers. This property avoids allocations by
        ///     using an internally cached list.
        /// </summary>
        public List<PromulPeer> ConnectedPeerList
        {
            get
            {
                CopyPeersIntoList(_connectedPeerListCache, ConnectionState.Connected);
                return _connectedPeerListCache;
            }
        }

        /// <summary>
        ///     Gets a peer by ID.
        /// </summary>
        public PromulPeer? GetPeerById(int id)
        {
            if (id >= 0 && id < _peersArray.Length)
            {
                return _peersArray[id];
            }

            return null;
        }

        /// <summary>
        ///     Gets a peer by ID.
        /// </summary>
        public bool TryGetPeerById(int id, out PromulPeer peer)
        {
            var tmp = GetPeerById(id);
            peer = tmp!;

            return tmp != null;
        }

        internal int ExtraPacketSizeForLayer => _extraPacketLayer?.ExtraPacketSizeForLayer ?? 0;

        private bool TryGetPeer(IPEndPoint endPoint, out PromulPeer peer)
        {
            bool result = _peers.TryGetValue(endPoint, out peer);
            return result;
        }

        private void AddPeer(PromulPeer peer)
        {
            if (_headPeer != null)
            {
                peer.NextPeer = _headPeer;
                _headPeer.PrevPeer = peer;
            }
            _headPeer = peer;
            _peers.Add(peer.EndPoint, peer);
            if (peer.Id >= _peersArray.Length)
            {
                int newSize = _peersArray.Length * 2;
                while (peer.Id >= newSize)
                    newSize *= 2;
                Array.Resize(ref _peersArray, newSize);
            }
            _peersArray[peer.Id] = peer;
        }

        private void RemovePeer(PromulPeer peer)
        {
            RemovePeerInternal(peer);
        }

        private void RemovePeerInternal(PromulPeer peer)
        {
            if (!_peers.Remove(peer.EndPoint))
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
        ///     Creates a new <see cref="PromulManager"/>.
        /// </summary>
        /// <param name="extraPacketLayer">
        ///     An extra packet processing layer, used for utilities like CRC checksum verification or encryption layers.
        ///     All <see cref="PromulManager"/> instances connected together must have the same configured layers.
        /// </param>
        public PromulManager(PacketLayerBase? extraPacketLayer = null)
        {
            //NatPunchModule = new NatPunchModule(this);
            _extraPacketLayer = extraPacketLayer;
        }

        internal async Task ConnectionLatencyUpdated(PromulPeer fromPeer, int latency)
        {
            if (OnNetworkLatencyUpdate != null) await OnNetworkLatencyUpdate(fromPeer, latency);
        }

        internal async Task MessageDelivered(PromulPeer fromPeer, object? userData)
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

        internal async Task<PromulPeer> OnConnectionRequestResolved(ConnectionRequest request, ArraySegment<byte> data)
        {
            PromulPeer PromulPeer = null;

            if (request.Result == ConnectionRequestResult.RejectForce)
            {
                NetDebug.Write(NetLogLevel.Trace, "[NM] Peer connect reject force.");
                if (data is { Array: not null, Count: > 0 })
                {
                    var shutdownPacket = NetworkPacket.FromProperty(PacketProperty.Disconnect, data.Count);
                    shutdownPacket.ConnectionNumber = request.InternalPacket.ConnectionNumber;
                    FastBitConverter.GetBytes(shutdownPacket.Data.Array, shutdownPacket.Data.Offset+1, request.InternalPacket.ConnectionTime);
                    if (shutdownPacket.Data.Count >= NetConstants.PossibleMtu[0])
                        NetDebug.WriteError("[Peer] Disconnect additional data size more than MTU!");
                    else data.CopyTo(shutdownPacket.Data.Array, shutdownPacket.Data.Offset+9);
                    await SendRaw(shutdownPacket, request.RemoteEndPoint);
                }
            }
            else
            {
                if (_peers.TryGetValue(request.RemoteEndPoint, out PromulPeer))
                {
                    //already have peer
                }
                else if (request.Result == ConnectionRequestResult.Reject)
                {
                    PromulPeer = new PromulPeer(this, request.RemoteEndPoint, GetNextPeerId());
                    await PromulPeer.RejectAsync(request.InternalPacket, data);
                    AddPeer(PromulPeer);
                    NetDebug.Write(NetLogLevel.Trace, "[NM] Peer connect reject.");
                }
                else //Accept
                {
                    PromulPeer = await PromulPeer.AcceptAsync(this, request, GetNextPeerId());
                    AddPeer(PromulPeer);
                    if (OnPeerConnected != null) await OnPeerConnected(PromulPeer);
                    NetDebug.Write(NetLogLevel.Trace, $"[NM] Received peer connection Id: {PromulPeer.ConnectTime}, EP: {PromulPeer.EndPoint}");
                }
            }

            _connectionRequests.Remove(request.RemoteEndPoint);

            return PromulPeer;
        }

        private int GetNextPeerId()
        {
            return _peerIds.TryDequeue(out int id) ? id : _lastPeerId++;
        }

        private async Task ProcessConnectRequest(
            IPEndPoint remoteEndPoint,
            PromulPeer PromulPeer,
            NetConnectRequestPacket connRequest)
        {
            //if we have peer
            if (PromulPeer != null)
            {
                var processResult = await PromulPeer.ProcessConnectRequest(connRequest);
                NetDebug.Write($"ConnectRequest LastId: {PromulPeer.ConnectTime}, NewId: {connRequest.ConnectionTime}, EP: {remoteEndPoint}, Result: {processResult}");

                switch (processResult)
                {
                    case ConnectRequestResult.Reconnection:
                        await ForceDisconnectPeerAsync(PromulPeer, DisconnectReason.Reconnect, 0, null);
                        RemovePeer(PromulPeer);
                        //go to new connection
                        break;
                    case ConnectRequestResult.NewConnection:
                        RemovePeer(PromulPeer);
                        //go to new connection
                        break;
                    case ConnectRequestResult.P2PLose:
                        await ForceDisconnectPeerAsync(PromulPeer, DisconnectReason.PeerToPeerConnection, 0, null);
                        RemovePeer(PromulPeer);
                        //go to new connection
                        break;
                    default:
                        //no operations needed
                        return;
                }
                //ConnectRequestResult.NewConnection
                //Set next connection number
                if(processResult != ConnectRequestResult.P2PLose)
                    connRequest.ConnectionNumber = (byte)((PromulPeer.ConnectionNum + 1) % NetConstants.MaxConnectionNumber);
                //To reconnect peer
            }
            else
            {
                NetDebug.Write($"ConnectRequest Id: {connRequest.ConnectionTime}, EP: {remoteEndPoint}");
            }

            ConnectionRequest req;
            lock (_connectionRequests)
            {
                if (_connectionRequests.TryGetValue(remoteEndPoint, out req))
                {
                    req.UpdateRequest(connRequest);
                    return;
                }
                req = new ConnectionRequest(remoteEndPoint, connRequest, this);
                _connectionRequests.Add(remoteEndPoint, req);
            }
            NetDebug.Write($"[NM] Creating request event: {connRequest.ConnectionTime}");
            if (OnConnectionRequest != null) await OnConnectionRequest(req);
        }

        private async Task OnMessageReceived(NetworkPacket packet, IPEndPoint remoteEndPoint)
        {
#if DEBUG
            if (SimulatePacketLoss && _randomGenerator.NextDouble() * 100 < SimulatePacketLossChance)
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

        private async Task DebugMessageReceived(NetworkPacket packet, IPEndPoint remoteEndPoint)
        {
#endif
            var originalPacketSize = packet.Data.Count;
            if (RecordNetworkStatistics)
            {
                Statistics.IncrementPacketsReceived();
                Statistics.AddBytesReceived(originalPacketSize);
            }

            if (_ntpRequests.Count > 0)
            {
                if (_ntpRequests.TryGetValue(remoteEndPoint, out var request))
                {
                    if (packet.Data.Count < 48)
                    {
                        NetDebug.Write(NetLogLevel.Trace, $"NTP response too short: {packet.Data.Count}");
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
                if (packet.Data.Count == 0)
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
                        await SendRaw(NetworkPacket.FromProperty(PacketProperty.InvalidProtocol, 0), remoteEndPoint);
                        return;
                    }
                    break;
                //unconnected messages
                case PacketProperty.Broadcast:
                    if (!BroadcastMessagesAllowed)
                        return;
                    await OnConnectionlessReceive(remoteEndPoint, packet.CreateReader(packet.GetHeaderSize()), UnconnectedMessageType.Broadcast);
                    return;
                case PacketProperty.UnconnectedMessage:
                    if (!ConnectionlessMessagesAllowed)
                        return;
                    
                    await OnConnectionlessReceive(remoteEndPoint, packet.CreateReader(packet.GetHeaderSize()), UnconnectedMessageType.BasicMessage);
                    return;
                case PacketProperty.NatMessage:
                    //if (NatPunchEnabled)
                    //    NatPunchModule.ProcessMessage(remoteEndPoint, packet);
                    return;
            }

            //Check normal packets
            bool peerFound = _peers.TryGetValue(remoteEndPoint, out var PromulPeer);

            if (peerFound && RecordNetworkStatistics)
            {
                PromulPeer.Statistics.IncrementPacketsReceived();
                PromulPeer.Statistics.AddBytesReceived(originalPacketSize);
            }
            
            switch (packet.Property)
            {
                case PacketProperty.ConnectRequest:
                    var connRequest = NetConnectRequestPacket.FromData(packet);
                    if (connRequest != null)
                        await ProcessConnectRequest(remoteEndPoint, PromulPeer, connRequest);
                    break;
                case PacketProperty.PeerNotFound:
                    if (peerFound) //local
                    {
                        if (PromulPeer.ConnectionState != ConnectionState.Connected)
                            return;
                        if (packet.Data.Count == 1)
                        {
                            //first reply
                            //send NetworkChanged packet
                            PromulPeer.ResetMtu();
                            await SendRaw(NetConnectAcceptPacket.MakeNetworkChanged(PromulPeer), remoteEndPoint);
                            NetDebug.Write($"PeerNotFound sending connection info: {remoteEndPoint}");
                        }
                        else if (packet.Data.Count == 2 && packet.Data.Array[packet.Data.Offset+1] == 1)
                        {
                            //second reply
                            await ForceDisconnectPeerAsync(PromulPeer, DisconnectReason.PeerNotFound, 0, null);
                        }
                    }
                    else if (packet.Data.Count > 1) //remote
                    {
                        //check if this is old peer
                        bool isOldPeer = false;

                        if (AllowPeerAddressChange)
                        {
                            NetDebug.Write($"[NM] Looks like address change: {packet.Data.Count}");
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
                            var secondResponse = NetworkPacket.FromProperty(PacketProperty.PeerNotFound, 1);
                            secondResponse.Data.Array[secondResponse.Data.Offset+1] = 1;
                            await SendRaw(secondResponse, remoteEndPoint);
                        }
                    }
                    break;
                case PacketProperty.InvalidProtocol:
                    if (peerFound && PromulPeer.ConnectionState == ConnectionState.Outgoing)
                        await ForceDisconnectPeerAsync(PromulPeer, DisconnectReason.InvalidProtocol, 0, null);
                    break;
                case PacketProperty.Disconnect:
                    if (peerFound)
                    {
                        var disconnectResult = PromulPeer.ProcessDisconnect(packet);
                        if (disconnectResult == DisconnectResult.None)
                        {
                            //PoolRecycle(packet);
                            return;
                        }
                        await ForceDisconnectPeerAsync(
                            PromulPeer,
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
                    await SendRaw(NetworkPacket.FromProperty(PacketProperty.ShutdownOk, 0), remoteEndPoint);
                    break;
                case PacketProperty.ConnectAccept:
                    if (!peerFound)
                        return;
                    var connAccept = NetConnectAcceptPacket.FromData(packet);
                    if (connAccept != null && PromulPeer.ProcessConnectAccept(connAccept))
                    {
                        if (OnPeerConnected != null) await OnPeerConnected(PromulPeer);
                    }
                    break;
                default:
                    if(peerFound)
                        PromulPeer.ProcessPacket(packet);
                    else
                        await SendRaw(NetworkPacket.FromProperty(PacketProperty.PeerNotFound, 0), remoteEndPoint);
                    break;
            }
        }

        internal async Task CreateReceiveEvent(NetworkPacket packet, DeliveryMethod method, byte channelNumber, int headerSize, PromulPeer fromPeer)
        {
            if (OnReceive != null) await OnReceive(fromPeer, packet.CreateReader(headerSize), channelNumber, method);
        }
        
        /// <summary>
        ///     Sends data to all connected peers using the given channel and delivery method.
        ///     If <see cref="excludePeer"/> is set, all peers excluding that specific peer will
        ///     receive the information.
        /// </summary>
        /// <param name="data">The data to send.</param>
        /// <param name="channelNumber">The channel number. This can range from 0 to <see cref="ChannelsCount"/> - 1.</param>
        /// <param name="options">The delivery method to utilise.</param>
        /// <param name="excludePeer">The (optional) peer to exclude from receiving this information.</param>
        public void SendToAll(ArraySegment<byte> data, DeliveryMethod options = DeliveryMethod.ReliableOrdered, byte channelNumber = 0, PromulPeer? excludePeer = null)
        {
            for (var PromulPeer = _headPeer; PromulPeer != null; PromulPeer = PromulPeer.NextPeer)
            {
                if (PromulPeer != excludePeer)
                    PromulPeer.Send(data, options, channelNumber);
            }
        }


        /// <summary>
        ///     Sends a connectionless message.
        /// </summary>
        /// <param name="data">The data to send.</param>
        /// <param name="remoteEndPoint">The destination.</param>
        /// <returns>Whether the send operation was successful.</returns>
        public async Task<bool> SendConnectionlessMessageAsync(ArraySegment<byte> data, IPEndPoint remoteEndPoint)
        {
            var packet = NetworkPacket.FromBuffer(data);
            packet.Property = PacketProperty.UnconnectedMessage;
            return await SendRaw(packet, remoteEndPoint) > 0;
        }

        /// <summary>
        ///     Connects to a remote host by IP address.
        /// </summary>
        /// <param name="target">The endpoint of the remote host.</param>
        /// <param name="connectionData">Additional data presented to the remote host.</param>
        /// <returns>The PromulPeer, if connection was successful. Returns null if we are waiting for a response.</returns>
        public async Task<PromulPeer?> ConnectAsync(IPEndPoint target, ArraySegment<byte> connectionData)
        {
            if (_connectionRequests.ContainsKey(target))
                return null;

            byte connectionNumber = 0;
            if (_peers.TryGetValue(target, out var peer))
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
            peer = await PromulPeer.ConnectToAsync(this, target, GetNextPeerId(), connectionNumber, connectionData);
            AddPeer(peer);

            return peer;
        }

        /// <summary>
        ///     Stops this manager, disconnecting all peers and closing the socket.
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
            for(var PromulPeer = _headPeer; PromulPeer != null; PromulPeer = PromulPeer.NextPeer)
                await PromulPeer.ShutdownAsync(null, !sendDisconnectMessages);

            //Stop
            CloseSocket();
            

            //clear peers
//             _peersLock.EnterWriteLock();
             _headPeer = null;
//             _peersDict.Clear();
             _peersArray = new PromulPeer[32];
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
        ///     Returns the number of peers in a given connection state.
        /// </summary>
        /// <param name="peerState">The state to query. Bit flags are supported.</param>
        /// <returns>The number of peers who are in the given state.</returns>
        public int GetNumberOfPeersInState(ConnectionState peerState)
        {
            int count = 0;
            for (var PromulPeer = _headPeer; PromulPeer != null; PromulPeer = PromulPeer.NextPeer)
            {
                if ((PromulPeer.ConnectionState & peerState) != 0)
                    count++;
            }
            return count;
        }

        /// <summary>
        ///     Copies all peers in a given state into the supplied list. This method avoids allocations.
        /// </summary>
        /// <param name="peers">This list will be cleared and populated with the output of the query.</param>
        /// <param name="peerState">The state to query. Bit flags are supported.</param>
        public void CopyPeersIntoList(List<PromulPeer> peers, ConnectionState peerState)
        {
            peers.Clear();
            for (var PromulPeer = _headPeer; PromulPeer != null; PromulPeer = PromulPeer.NextPeer)
            {
                if ((PromulPeer.ConnectionState & peerState) != 0)
                    peers.Add(PromulPeer);
            }
        }

        /// <summary>
        ///     Gracefully disconnects all peers.
        /// </summary>
        /// <param name="data">The shutdown message to be sent to each peer.
        /// As only one message is sent, the size of this data must be less than or equal to the current MTU.</param>
        public async Task DisconnectAllPeersAsync(ArraySegment<byte> data = default)
        {
            //Send disconnect packets
            for (var PromulPeer = _headPeer; PromulPeer != null; PromulPeer = PromulPeer.NextPeer)
            {
                await DisconnectPeerAsync(PromulPeer, data);
            }
        }

        /// <summary>
        ///     Gracefully disconnects a given peer.
        /// </summary>
        /// <param name="peer">The peer to disconnect.</param>
        /// <param name="data">The shutdown message to be sent to each peer.
        /// As only one message is sent, the size of this data must be less than or equal to the current MTU.</param>
        public Task DisconnectPeerAsync(PromulPeer peer, ArraySegment<byte> data = default)
        {
            return DisconnectPeerInternalAsync(
                peer,
                DisconnectReason.DisconnectPeerCalled,
                0,
                false,
                data,
                null);
        }
        
        /// <summary>
        ///     Immediately disconnects a given peer without providing them with additional data.
        /// </summary>
        /// <param name="peer">The peer to disconnect.</param>
        public Task ForceDisconnectPeerAsync(PromulPeer peer, DisconnectReason reason = DisconnectReason.DisconnectPeerCalled, SocketError errorCode = 0, NetworkPacket? data = null)
        {
            return DisconnectPeerInternalAsync(peer, reason, errorCode, true, null, data);
        }
        
        private async Task DisconnectPeerInternalAsync(
            PromulPeer peer,
            DisconnectReason reason,
            SocketError socketErrorCode,
            bool force,
            ArraySegment<byte> data,
            NetworkPacket? eventData)
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
                AdditionalData = eventData.CreateReader(eventData?.GetHeaderSize() ?? 0) });
        }
        
        /// <summary>
        ///     Creates a NTP request for a given address and port.
        /// </summary>
        /// <param name="ntpServerAddress">The server address of the desired NTP server.</param>
        /// <param name="port">The port of the desired NTP server, or, the default NTP port (123).</param>
        public void CreateNtpRequest(string ntpServerAddress, int port = NtpRequest.DefaultPort)
        {
            IPEndPoint endPoint = NetUtils.MakeEndPoint(ntpServerAddress, NtpRequest.DefaultPort);
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }
    }
}
