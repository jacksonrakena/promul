using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private readonly SemaphoreSlim _pingSimulationSemaphore = new SemaphoreSlim(1, 1);
        private readonly List<IncomingData> _pingSimulationList = new List<IncomingData>();
        private readonly Random _randomGenerator = new Random();
        private const int MinLatencyThreshold = 5;
#endif
        
        private readonly Dictionary<IPEndPoint, PeerBase> _peers = new(new IPEndPointComparer());
        private readonly Dictionary<IPEndPoint, ConnectionRequest> _connectionRequests = new(new IPEndPointComparer());
        private readonly ConcurrentDictionary<IPEndPoint, NtpRequest> _ntpRequests = new(new IPEndPointComparer());
        private volatile PeerBase? _headPeer;
        private int _connectedPeersCount;
        private readonly List<PeerBase> _connectedPeerListCache = new();
        private PeerBase?[] _peersArray = new PeerBase[32];
        private readonly PacketLayerBase? _extraPacketLayer;
        private int _lastPeerId;
        private ConcurrentQueue<int> _peerIds = new();
        private byte _channelsCount = 1;

        /// <summary>
        ///     Allow messages to be received from peers that the manager has not established a connection with.
        ///     To send a connectionless message, call <see cref="SendConnectionlessMessageAsync"/>.
        /// </summary>
        public bool ConnectionlessMessagesAllowed { get; set; }

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
        public bool SimulatePacketLoss { get; set; }

        /// <summary>
        ///     Whether to simulate latency by holding packets for a random period of time before sending.
        ///     This setting will not do anything if the DEBUG constant is not defined.
        /// </summary>
        public bool SimulateLatency { get; set; }

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
        public PeerBase? FirstPeer => _headPeer;

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
        public List<PeerBase> ConnectedPeerList
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
        public PeerBase? GetPeerById(int id)
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
        public bool TryGetPeerById(int id, out PeerBase peer)
        {
            var tmp = GetPeerById(id);
            peer = tmp!;

            return tmp != null;
        }

        internal int ExtraPacketSizeForLayer => _extraPacketLayer?.ExtraPacketSizeForLayer ?? 0;

        private bool TryGetPeer(IPEndPoint endPoint, out PeerBase peer)
        {
            bool result = _peers.TryGetValue(endPoint, out peer);
            return result;
        }

        private void AddPeer(PeerBase peer)
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

        private void RemovePeer(PeerBase peer)
        {
            RemovePeerInternal(peer);
        }

        private void RemovePeerInternal(PeerBase peer)
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

        internal async Task ConnectionLatencyUpdated(PeerBase fromPeer, int latency)
        {
            if (OnNetworkLatencyUpdate != null) await OnNetworkLatencyUpdate(fromPeer, latency);
        }

        internal async Task MessageDelivered(PeerBase fromPeer, object? userData)
        {
            if (OnMessageDelivered != null) await OnMessageDelivered(fromPeer, userData);
        }
        

        private async Task ProcessDelayedPackets()
        {
#if DEBUG
            if (!SimulateLatency)
                return;

            var time = DateTime.UtcNow;
            await _pingSimulationSemaphore.WaitAsync();
            try
            {
                for (int i = 0; i < _pingSimulationList.Count; i++)
                {
                    var incomingData = _pingSimulationList[i];
                    if (incomingData.TimeWhenGet <= time)
                    {
                        await DebugMessageReceived(incomingData.Data, incomingData.EndPoint);
                        _pingSimulationList.RemoveAt(i);
                        i--;
                    }
                }
            }
            finally
            {
                _pingSimulationSemaphore.Release(); 
            }
#endif
        }

        private void ProcessNtpRequests(long elapsedMilliseconds)
        {
            List<IPEndPoint>? requestsToRemove = null;
            foreach (var ntpRequest in _ntpRequests)
            {
                if (_udpSocketv4 != null) ntpRequest.Value.Send(_udpSocketv4, elapsedMilliseconds);
                if (!ntpRequest.Value.NeedToKill) continue;
                requestsToRemove ??= new List<IPEndPoint>();
                requestsToRemove.Add(ntpRequest.Key);
            }

            if (requestsToRemove == null) return;
            foreach (var ipEndPoint in requestsToRemove)
            {
                _ntpRequests.TryRemove(ipEndPoint, out _);
            }
        }

        internal async Task<PeerBase?> OnConnectionRequestResolved(ConnectionRequest request, ArraySegment<byte> data)
        {
            PeerBase? peer = null;
            
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
                    await RawSendAsync(shutdownPacket, request.RemoteEndPoint);
                }
            }
            else
            {
                if (_peers.TryGetValue(request.RemoteEndPoint, out peer))
                {
                    //already have peer
                }
                else if (request.Result == ConnectionRequestResult.Reject)
                {
                    peer = new IncomingPeer(this, request.RemoteEndPoint, GetNextPeerId(), 
                        request.InternalPacket.PeerId, request.InternalPacket.ConnectionTime, 
                        request.InternalPacket.ConnectionNumber);
                    
                    await peer.ShutdownAsync(data, false);
                    AddPeer(peer);
                    NetDebug.Write(NetLogLevel.Trace, "[NM] Peer connect reject.");
                }
                else // Accept
                {
                    peer = await PeerBase.AcceptAsync(this, request, GetNextPeerId());
                    AddPeer(peer);
                    if (OnPeerConnected != null) await OnPeerConnected(peer);
                    NetDebug.Write(NetLogLevel.Trace, $"[NM] Received peer connection Id: {peer.ConnectTime}, EP: {peer.EndPoint}");
                }
            }

            _connectionRequests.Remove(request.RemoteEndPoint);

            return peer;
        }

        private int GetNextPeerId()
        {
            return _peerIds.TryDequeue(out int id) ? id : _lastPeerId++;
        }

        private async Task ProcessConnectRequest(
            IPEndPoint remoteEndPoint,
            PeerBase? peer,
            NetConnectRequestPacket connRequest)
        {
            //if we have peer
            if (peer != null)
            {
                var processResult = await peer.ProcessConnectionRequestAsync(connRequest);
                NetDebug.Write($"ConnectRequest LastId: {peer.ConnectTime}, NewId: {connRequest.ConnectionTime}, EP: {remoteEndPoint}, Result: {processResult}");

                switch (processResult)
                {
                    case ConnectRequestResult.Reconnection:
                        await ForceDisconnectPeerAsync(peer, DisconnectReason.Reconnect, 0, null);
                        RemovePeer(peer);
                        //go to new connection
                        break;
                    case ConnectRequestResult.NewConnection:
                        RemovePeer(peer);
                        //go to new connection
                        break;
                    case ConnectRequestResult.P2PLose:
                        await ForceDisconnectPeerAsync(peer, DisconnectReason.PeerToPeerConnection, 0, null);
                        RemovePeer(peer);
                        //go to new connection
                        break;
                    default:
                        //no operations needed
                        return;
                }
                //ConnectRequestResult.NewConnection
                //Set next connection number
                if(processResult != ConnectRequestResult.P2PLose)
                    connRequest.ConnectionNumber = (byte)((peer.ConnectionNumber + 1) % NetConstants.MaxConnectionNumber);
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

            // For messages that don't use the peer topology,
            // handle those before checking peers to save some cycles
            switch (packet.Property)
            {
                case PacketProperty.Broadcast:
                    if (!BroadcastMessagesAllowed) return;
                    await OnConnectionlessReceive(remoteEndPoint, packet.CreateReader(packet.GetHeaderSize()), UnconnectedMessageType.Broadcast);
                    return;
                case PacketProperty.UnconnectedMessage:
                    if (!ConnectionlessMessagesAllowed) return;
                    await OnConnectionlessReceive(remoteEndPoint, packet.CreateReader(packet.GetHeaderSize()), UnconnectedMessageType.BasicMessage);
                    return;
                // TODO: NAT punching
            }

            var peerFound = _peers.TryGetValue(remoteEndPoint, out var memoryPeer);

            if (peerFound && RecordNetworkStatistics)
            {
                memoryPeer!.Statistics.IncrementPacketsReceived();
                memoryPeer.Statistics.AddBytesReceived(originalPacketSize);
            }
            
            switch (packet.Property)
            {
                // Handle connection requests
                case PacketProperty.ConnectRequest:
                    if (NetConnectRequestPacket.GetProtocolId(packet) != NetConstants.ProtocolId)
                    {
                        await RawSendAsync(NetworkPacket.FromProperty(PacketProperty.InvalidProtocol, 0), remoteEndPoint);
                        return;
                    }
                    var connRequest = NetConnectRequestPacket.FromData(packet);
                    if (connRequest != null) await ProcessConnectRequest(remoteEndPoint, memoryPeer, connRequest);
                    break;
                case PacketProperty.PeerNotFound:
                    if (peerFound)
                    {
                        if (memoryPeer!.ConnectionState != ConnectionState.Connected)
                            return;
                        if (packet.Data.Count == 1)
                        {
                            //first reply
                            //send NetworkChanged packet
                            memoryPeer.ResetMtu();
                            await RawSendAsync(NetConnectAcceptPacket.MakeNetworkChanged(memoryPeer), remoteEndPoint);
                            NetDebug.Write($"PeerNotFound sending connection info: {remoteEndPoint}");
                        }
                        else if (packet.Data.Count == 2 && packet.Data.Array[packet.Data.Offset+1] == 1)
                        {
                            //second reply
                            await ForceDisconnectPeerAsync(memoryPeer, DisconnectReason.PeerNotFound, 0, null);
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
                                    peer.ConnectionNumber == remoteData.ConnectionNumber)
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

                        //else peer really not found
                        if (!isOldPeer)
                        {
                            var secondResponse = NetworkPacket.FromProperty(PacketProperty.PeerNotFound, 1);
                            secondResponse.Data.Array[secondResponse.Data.Offset+1] = 1;
                            await RawSendAsync(secondResponse, remoteEndPoint);
                        }
                    }
                    break;
                // Remote rejected us because we sent an invalid protocol ID
                case PacketProperty.InvalidProtocol:
                    if (peerFound && memoryPeer!.ConnectionState == ConnectionState.Outgoing)
                        await ForceDisconnectPeerAsync(memoryPeer, DisconnectReason.InvalidProtocol, 0, null);
                    break;
                case PacketProperty.Disconnect:
                    if (peerFound)
                    {
                        var disconnectResult = memoryPeer!.ProcessDisconnect(packet);
                        if (disconnectResult == DisconnectResult.None)
                        {
                            return;
                        }
                        await ForceDisconnectPeerAsync(
                            memoryPeer,
                            disconnectResult == DisconnectResult.Disconnect
                            ? DisconnectReason.RemoteConnectionClose
                            : DisconnectReason.ConnectionRejected,
                            0, packet);
                    }
                    
                    await RawSendAsync(NetworkPacket.FromProperty(PacketProperty.ShutdownOk, 0), remoteEndPoint);
                    break;
                case PacketProperty.ConnectAccept:
                    if (!peerFound) return;
                    var connAccept = NetConnectAcceptPacket.FromData(packet);
                    if (connAccept != null && memoryPeer is OutgoingPeer op && op.ProcessConnectionAccepted(connAccept))
                    {
                        if (OnPeerConnected != null) await OnPeerConnected(memoryPeer);
                    }
                    break;
                default:
                    if (peerFound) await memoryPeer!.ProcessPacket(packet);
                    else
                    {
                        NetDebug.Write($"Unknown peer attempted to send {packet.Property} with {packet.Data.Count} bytes of data.");
                        await RawSendAsync(NetworkPacket.FromProperty(PacketProperty.PeerNotFound, 0), remoteEndPoint);   
                    }
                    break;
            }
        }

        internal async Task CreateReceiveEvent(NetworkPacket packet, DeliveryMethod method, byte channelNumber, int headerSize, PeerBase fromPeer)
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
        public async Task SendToAllAsync(ArraySegment<byte> data, DeliveryMethod options = DeliveryMethod.ReliableOrdered, byte channelNumber = 0, PeerBase? excludePeer = null)
        {
            for (var peer = _headPeer; peer != null; peer = peer.NextPeer)
            {
                if (peer != excludePeer)
                    await peer.SendAsync(data, options, channelNumber);
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
            return await RawSendAsync(packet, remoteEndPoint) > 0;
        }

        /// <summary>
        ///     Connects to a remote host by IP address.
        /// </summary>
        /// <param name="target">The endpoint of the remote host.</param>
        /// <param name="connectionData">Additional data presented to the remote host.</param>
        /// <returns>The PromulPeer, if connection was successful. Returns null if we are waiting for a response.</returns>
        public async Task<PeerBase?> ConnectAsync(IPEndPoint target, ArraySegment<byte> connectionData)
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
                connectionNumber = (byte)((peer.ConnectionNumber + 1) % NetConstants.MaxConnectionNumber);
                RemovePeer(peer);
            }

            //Create reliable connection
            //And send connection request
            peer = await PeerBase.ConnectToAsync(this, target, GetNextPeerId(), connectionNumber, connectionData);
            AddPeer(peer);

            return peer;
        }

        /// <summary>
        ///     Stops this manager, disconnecting all peers and closing the socket.
        /// </summary>
        /// <param name="sendDisconnectMessages">Whether to notify peers of pending disconnection.</param>
        public async Task StopAsync(bool sendDisconnectMessages = true)
        {
            NetDebug.Write("[Control] Stopping.");

#if UNITY_2018_3_OR_NEWER
            _pausedSocketFix.Deinitialize();
            _pausedSocketFix = null;
#endif

            // Disconnect all peers
            for (var peer = _headPeer; peer != null; peer = peer.NextPeer) await peer.ShutdownAsync(null, !sendDisconnectMessages);

            CloseSocket();
            
            _headPeer = null;
            _peers.Clear();
            _peersArray = new PeerBase[32];
            _peerIds = new ConcurrentQueue<int>();
            _lastPeerId = 0;
#if DEBUG
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
            for (var peer = _headPeer; peer != null; peer = peer.NextPeer)
            {
                if ((peer.ConnectionState & peerState) != 0)
                    count++;
            }
            return count;
        }

        /// <summary>
        ///     Copies all peers in a given state into the supplied list. This method avoids allocations.
        /// </summary>
        /// <param name="peers">This list will be cleared and populated with the output of the query.</param>
        /// <param name="peerState">The state to query. Bit flags are supported.</param>
        public void CopyPeersIntoList(List<PeerBase> peers, ConnectionState peerState)
        {
            peers.Clear();
            for (var peer = _headPeer; peer != null; peer = peer.NextPeer)
            {
                if ((peer.ConnectionState & peerState) != 0)
                    peers.Add(peer);
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
            for (var peer = _headPeer; peer != null; peer = peer.NextPeer)
            {
                await DisconnectPeerAsync(peer, data);
            }
        }

        /// <summary>
        ///     Gracefully disconnects a given peer.
        /// </summary>
        /// <param name="peer">The peer to disconnect.</param>
        /// <param name="data">The shutdown message to be sent to each peer.
        /// As only one message is sent, the size of this data must be less than or equal to the current MTU.</param>
        public Task DisconnectPeerAsync(PeerBase peer, ArraySegment<byte> data = default)
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
        /// <param name="reason">The reason for disconnection.</param>
        /// <param name="errorCode">The socket error code.</param>
        /// <param name="data"></param>
        public Task ForceDisconnectPeerAsync(PeerBase peer, 
            DisconnectReason reason = DisconnectReason.DisconnectPeerCalled,
            SocketError errorCode = 0, 
            NetworkPacket? data = null)
        {
            return DisconnectPeerInternalAsync(peer, reason, errorCode, true, null, data);
        }
        
        private async Task DisconnectPeerInternalAsync(
            PeerBase peer,
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
                AdditionalData = eventData?.CreateReader(eventData?.GetHeaderSize() ?? 0) });
        }
        
        /// <summary>
        ///     Creates a NTP request for a given address and port.
        /// </summary>
        /// <param name="ntpServerAddress">The server address of the desired NTP server.</param>
        /// <param name="port">The port of the desired NTP server, or, the default NTP port (123).</param>
        public void CreateNtpRequest(string ntpServerAddress, int port = NtpRequest.DefaultPort)
        {
            var endPoint = NetUtils.MakeEndPoint(ntpServerAddress, NtpRequest.DefaultPort);
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }
    }
}
