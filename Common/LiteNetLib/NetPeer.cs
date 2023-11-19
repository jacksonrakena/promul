#if DEBUG
#define STATS_ENABLED
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    /// <summary>
    /// Peer connection state
    /// </summary>
    [Flags]
    public enum ConnectionState : byte
    {
        Outgoing         = 1 << 1,
        Connected         = 1 << 2,
        ShutdownRequested = 1 << 3,
        Disconnected      = 1 << 4,
        EndPointChange    = 1 << 5,
        Any = Outgoing | Connected | ShutdownRequested | EndPointChange
    }

    internal enum ConnectRequestResult
    {
        None,
        P2PLose, //when peer connecting
        Reconnection,  //when peer was connected
        NewConnection  //when peer was disconnected
    }

    internal enum DisconnectResult
    {
        None,
        Reject,
        Disconnect
    }

    internal enum ShutdownResult
    {
        None,
        Success,
        WasConnected
    }

    /// <summary>
    /// Network peer. Main purpose is sending messages to specific peer.
    /// </summary>
    public class NetPeer
    {
        //Ping and RTT
        private int _rtt;
        private int _rttCount;
        private long _pingSendTimer;
        private long _rttResetTimer;
        private readonly Stopwatch _pingTimer = new Stopwatch();
        private long _timeSinceLastPacket;

        //Common
        readonly SemaphoreSlim _shutdownSemaphore = new SemaphoreSlim(1, 1);
        internal volatile NetPeer? NextPeer;
        internal NetPeer? PrevPeer;

        internal byte ConnectionNum
        {
            get => _connectNum;
            private set
            {
                _connectNum = value;
                _mergeData.ConnectionNumber = value;
                _pingPacket.ConnectionNumber = value;
                _pongPacket.ConnectionNumber = value;
            }
        }

        //Channels
        private readonly Queue<NetPacket> _unreliableChannel;
        SemaphoreSlim _unreliableChannelSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<BaseChannel> _channelSendQueue;
        private readonly BaseChannel?[] _channels;

        //MTU
        private int _mtuIdx;
        private bool _finishMtu;
        private long _mtuCheckTimer;
        private int _mtuCheckAttempts;
        private const int MtuCheckDelay = 1000;
        private const int MaxMtuCheckAttempts = 4;
        private readonly SemaphoreSlim _mtuMutex = new SemaphoreSlim(1,1);

        //Fragment
        private class IncomingFragments
        {
            public NetPacket[] Fragments;
            public int ReceivedCount;
            public int TotalSize;
            public byte ChannelId;
        }
        private int _fragmentId;
        private readonly Dictionary<ushort, IncomingFragments> _holdedFragments;
        private readonly Dictionary<ushort, ushort> _deliveredFragments;

        //Merging
        private readonly NetPacket _mergeData;
        private int _mergePos;
        private int _mergeCount;

        //Connection
        private int _connectAttempts;
        private long _connectTimer;
        private byte _connectNum;
        private NetPacket? _shutdownPacket;
        private const int ShutdownDelay = 300;
        private long _shutdownTimer;
        private readonly NetPacket _pingPacket;
        private readonly NetPacket _pongPacket;
        private NetPacket? _connectRequestPacket;
        private NetPacket? _connectAcceptPacket;

        /// <summary>
        /// Peer ip address and port
        /// </summary>
        public IPEndPoint EndPoint { get; private set; }

        /// <summary>
        /// Peer parent NetManager
        /// </summary>
        public readonly NetManager NetManager;

        /// <summary>
        /// Current connection state
        /// </summary>
        public ConnectionState ConnectionState { get; private set; }

        /// <summary>
        /// Connection time for internal purposes
        /// </summary>
        internal long ConnectTime { get; private set; }

        /// <summary>
        /// Peer id can be used as key in your dictionary of peers
        /// </summary>
        public readonly int Id;

        /// <summary>
        /// Id assigned from server
        /// </summary>
        public int RemoteId { get; private set; }

        /// <summary>
        /// Current one-way ping (RTT/2) in milliseconds
        /// </summary>
        public int Ping => RoundTripTime/2;

        /// <summary>
        /// Round trip time in milliseconds
        /// </summary>
        public int RoundTripTime { get; private set; }

        /// <summary>
        /// Current MTU - Maximum Transfer Unit ( maximum udp packet size without fragmentation )
        /// </summary>
        public int Mtu { get; private set; }

        /// <summary>
        /// Delta with remote time in ticks (not accurate)
        /// positive - remote time > our time
        /// </summary>
        public long RemoteTimeDelta { get; private set; }

        /// <summary>
        /// Remote UTC time (not accurate)
        /// </summary>
        public DateTime RemoteUtcTime => new DateTime(DateTime.UtcNow.Ticks + RemoteTimeDelta);

        /// <summary>
        /// Time since last packet received (including internal library packets)
        /// </summary>
        public long TimeSinceLastPacket => _timeSinceLastPacket;

        internal double ResendDelay { get; private set; } = 27.0;

        /// <summary>
        /// Application defined object containing data about the connection
        /// </summary>
        public object? Tag;

        /// <summary>
        /// Statistics of peer connection
        /// </summary>
        public readonly NetStatistics Statistics;

        //incoming connection constructor
        internal NetPeer(NetManager netManager, IPEndPoint remoteEndPoint, int id)
        {
            Id = id;
            Statistics = new NetStatistics();
            NetManager = netManager;
            ResetMtu();
            EndPoint = remoteEndPoint;
            ConnectionState = ConnectionState.Connected;
            _mergeData = new NetPacket(PacketProperty.Merged, NetConstants.MaxPacketSize);
            _pongPacket = new NetPacket(PacketProperty.Pong, 0);
            _pingPacket = new NetPacket(PacketProperty.Ping, 0) {Sequence = 1};

            _unreliableChannel = new Queue<NetPacket>();
            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
            _deliveredFragments = new Dictionary<ushort, ushort>();

            _channels = new BaseChannel[netManager.ChannelsCount * NetConstants.ChannelTypeCount];
            _channelSendQueue = new ConcurrentQueue<BaseChannel>();
        }

        internal void InitiateEndPointChange()
        {
            ResetMtu();
            ConnectionState = ConnectionState.EndPointChange;
        }

        internal void FinishEndPointChange(IPEndPoint newEndPoint)
        {
            if (ConnectionState != ConnectionState.EndPointChange)
                return;
            ConnectionState = ConnectionState.Connected;
            EndPoint = newEndPoint;
        }

        internal void ResetMtu()
        {
            _finishMtu = false;
            if (NetManager.MtuOverride > 0)
                OverrideMtu(NetManager.MtuOverride);
            else if (NetManager.UseSafeMtu)
                SetMtu(0);
            else
                SetMtu(1);
        }

        private void SetMtu(int mtuIdx)
        {
            _mtuIdx = mtuIdx;
            Mtu = NetConstants.PossibleMtu[mtuIdx] - NetManager.ExtraPacketSizeForLayer;
        }

        private void OverrideMtu(int mtuValue)
        {
            Mtu = mtuValue;
            _finishMtu = true;
        }

        /// <summary>
        /// Returns packets count in queue for reliable channel
        /// </summary>
        /// <param name="channelNumber">number of channel 0-63</param>
        /// <param name="ordered">type of channel ReliableOrdered or ReliableUnordered</param>
        /// <returns>packets count in channel queue</returns>
        public int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered)
        {
            int idx = channelNumber * NetConstants.ChannelTypeCount +
                       (byte) (ordered ? DeliveryMethod.ReliableOrdered : DeliveryMethod.ReliableUnordered);
            var channel = _channels[idx];
            return channel != null ? ((ReliableChannel)channel).PacketsInQueue : 0;
        }

        /// <summary>
        /// Create temporary packet (maximum size MTU - headerSize) to send later without additional copies
        /// </summary>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <returns>PooledPacket that you can use to write data starting from UserDataOffset</returns>
        public PooledPacket CreatePacketFromPool(DeliveryMethod deliveryMethod, byte channelNumber)
        {
            //multithreaded variable
            int mtu = Mtu;
            var packet = NetManager.PoolGetPacket(mtu);
            if (deliveryMethod == DeliveryMethod.Unreliable)
            {
                packet.Property = PacketProperty.Unreliable;
                return new PooledPacket(packet, mtu, 0);
            }
            else
            {
                packet.Property = PacketProperty.Channeled;
                return new PooledPacket(packet, mtu, (byte)(channelNumber * NetConstants.ChannelTypeCount + (byte)deliveryMethod));
            }
        }

        /// <summary>
        /// Sends pooled packet without data copy
        /// </summary>
        /// <param name="packet">packet to send</param>
        /// <param name="userDataSize">size of user data you want to send</param>
        public void SendPooledPacket(PooledPacket packet, int userDataSize)
        {
            if (ConnectionState != ConnectionState.Connected)
                return;
            packet._packet.Size = packet.UserDataOffset + userDataSize;
            if (packet._packet.Property == PacketProperty.Channeled)
            {
                CreateChannel(packet._channelNumber).AddToQueue(packet._packet);
            }
            else
            {
                _unreliableChannelSemaphore.Wait();
                _unreliableChannel.Enqueue(packet._packet);
                _unreliableChannelSemaphore.Release();
            }
        }

        private BaseChannel CreateChannel(byte idx)
        {
            BaseChannel newChannel = _channels[idx];
            if (newChannel != null)
                return newChannel;
            switch ((DeliveryMethod)(idx % NetConstants.ChannelTypeCount))
            {
                case DeliveryMethod.ReliableUnordered:
                    newChannel = new ReliableChannel(this, false, idx);
                    break;
                case DeliveryMethod.Sequenced:
                    newChannel = new SequencedChannel(this, false, idx);
                    break;
                case DeliveryMethod.ReliableOrdered:
                    newChannel = new ReliableChannel(this, true, idx);
                    break;
                case DeliveryMethod.ReliableSequenced:
                    newChannel = new SequencedChannel(this, true, idx);
                    break;
            }
            BaseChannel prevChannel = Interlocked.CompareExchange(ref _channels[idx], newChannel, null);
            if (prevChannel != null)
                return prevChannel;

            return newChannel;
        }

        public static async Task<NetPeer> ConnectToAsync(NetManager manager, IPEndPoint remote, int id, byte connectionNumber, ArraySegment<byte> data)
        {
            var time = DateTime.UtcNow.Ticks;
            var packet = NetConnectRequestPacket.Make(data, remote.Serialize(), time, id);
            packet.ConnectionNumber = connectionNumber;
            var peer = new NetPeer(manager, remote, id, time, connectionNumber)
            {
                ConnectionState = LiteNetLib.ConnectionState.Outgoing,
                _connectRequestPacket = packet
            };

            await manager.SendRaw(peer._connectRequestPacket, peer.EndPoint);

            NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {peer.ConnectTime}");
            return peer;
        }
        
        public static async Task<NetPeer> AcceptAsync(NetManager netManager, ConnectionRequest request, int id)
        {
            var peer = new NetPeer(netManager, request.RemoteEndPoint, id, request.InternalPacket.ConnectionTime, request.InternalPacket.ConnectionNumber)
            {
                RemoteId = request.InternalPacket.PeerId,
                _connectAcceptPacket = NetConnectAcceptPacket.Make(request.InternalPacket.ConnectionTime, request.InternalPacket.ConnectionNumber, id),
                ConnectionState = ConnectionState.Connected
            };
            await netManager.SendRaw(peer._connectAcceptPacket, peer.EndPoint);

            NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {peer.ConnectTime}");
            return peer;
        }

        private NetPeer(NetManager netManager, IPEndPoint remote, int id,
            long connectTime, byte connectionNumber)
        {
            Id = id;
            Statistics = new NetStatistics();
            NetManager = netManager;
            ResetMtu();
            EndPoint = remote;
            ConnectionState = ConnectionState.Connected;
            _mergeData = new NetPacket(PacketProperty.Merged, NetConstants.MaxPacketSize);
            _pongPacket = new NetPacket(PacketProperty.Pong, 0);
            _pingPacket = new NetPacket(PacketProperty.Ping, 0) {Sequence = 1};

            _unreliableChannel = new Queue<NetPacket>();
            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
            _deliveredFragments = new Dictionary<ushort, ushort>();

            _channels = new BaseChannel[netManager.ChannelsCount * NetConstants.ChannelTypeCount];
            _channelSendQueue = new ConcurrentQueue<BaseChannel>();

            ConnectTime = connectTime;
            ConnectionNum = connectionNumber;
        }

        //Reject
        internal async Task RejectAsync(NetConnectRequestPacket requestData, ArraySegment<byte> data)
        {
            ConnectTime = requestData.ConnectionTime;
            _connectNum = requestData.ConnectionNumber;
            await ShutdownAsync(data, false);
        }

        internal bool ProcessConnectAccept(NetConnectAcceptPacket packet)
        {
            if (ConnectionState != ConnectionState.Outgoing)
                return false;

            //check connection id
            if (packet.ConnectionTime != ConnectTime)
            {
                NetDebug.Write(NetLogLevel.Trace, $"[NC] Invalid connectId: {packet.ConnectionTime} != our({ConnectTime})");
                return false;
            }
            //check connect num
            ConnectionNum = packet.ConnectionNumber;
            RemoteId = packet.PeerId;

            NetDebug.Write(NetLogLevel.Trace, "[NC] Received connection accept");
            Interlocked.Exchange(ref _timeSinceLastPacket, 0);
            ConnectionState = ConnectionState.Connected;
            return true;
        }

        /// <summary>
        /// Gets maximum size of packet that will be not fragmented.
        /// </summary>
        /// <param name="options">Type of packet that you want send</param>
        /// <returns>size in bytes</returns>
        public int GetMaxSinglePacketSize(DeliveryMethod options)
        {
            return Mtu - NetPacket.GetHeaderSize(options == DeliveryMethod.Unreliable ? PacketProperty.Unreliable : PacketProperty.Channeled);
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(ArraySegment<byte> data, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered, byte channelNumber = 0)
        {
            SendInternal(data, channelNumber, deliveryMethod);
        }

        private void SendInternal(
            ArraySegment<byte> data,
            byte channelNumber,
            DeliveryMethod deliveryMethod)
        {
            var length = data.Count;
            if (ConnectionState != ConnectionState.Connected || channelNumber >= _channels.Length)
                return;

            //Select channel
            PacketProperty property;
            BaseChannel?  channel = null;

            if (deliveryMethod == DeliveryMethod.Unreliable)
            {
                property = PacketProperty.Unreliable;
            }
            else
            {
                property = PacketProperty.Channeled;
                channel = CreateChannel((byte)(channelNumber * NetConstants.ChannelTypeCount + (byte)deliveryMethod));
            }

            //Prepare
            NetDebug.Write("[RS]Packet: " + property);

            //Check fragmentation
            int headerSize = NetPacket.GetHeaderSize(property);
            //Save mtu for multithread
            int mtu = Mtu;
            if (data.Count + headerSize > mtu)
            {
                //if cannot be fragmented
                if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                    throw new TooBigPacketException("Unreliable or ReliableSequenced packet size exceeded maximum of " + (mtu - headerSize) + " bytes, Check allowed size by GetMaxSinglePacketSize()");

                int packetFullSize = mtu - headerSize;
                int packetDataSize = packetFullSize - NetConstants.FragmentHeaderSize;
                int totalPackets = data.Count / packetDataSize + (data.Count % packetDataSize == 0 ? 0 : 1);

                NetDebug.Write($@"FragmentSend:
 MTU: {mtu}
 headerSize: {headerSize}
 packetFullSize: {packetFullSize}
 packetDataSize: {packetDataSize}
 totalPackets: {totalPackets}");

                if (totalPackets > ushort.MaxValue)
                    throw new TooBigPacketException("Data was split in " + totalPackets + " fragments, which exceeds " + ushort.MaxValue);

                ushort currentFragmentId = (ushort)Interlocked.Increment(ref _fragmentId);

                for(ushort partIdx = 0; partIdx < totalPackets; partIdx++)
                {
                    int sendLength = data.Count > packetDataSize ? packetDataSize : data.Count;

                    NetPacket p = NetManager.PoolGetPacket(headerSize + sendLength + NetConstants.FragmentHeaderSize);
                    p.Property = property;
                    p.FragmentId = currentFragmentId;
                    p.FragmentPart = partIdx;
                    p.FragmentsTotal = (ushort)totalPackets;
                    p.MarkFragmented();

                    if (data.Array != null) Buffer.BlockCopy(data.Array, data.Offset + partIdx * packetDataSize, p.RawData, NetConstants.FragmentedHeaderTotalSize, sendLength);
                    channel?.AddToQueue(p);

                    length -= sendLength;
                }
                return;
            }

            //Else just send
            NetPacket packet = NetManager.PoolGetPacket(headerSize + length);
            packet.Property = property;
            if (data.Array != null) Buffer.BlockCopy(data.Array, data.Offset, packet.RawData, headerSize, length);

            if (channel == null) //unreliable
            {
                _unreliableChannelSemaphore.Wait();
                _unreliableChannel.Enqueue(packet);
                _unreliableChannelSemaphore.Release();
            }
            else
            {
                channel.AddToQueue(packet);
            }
        }

        internal DisconnectResult ProcessDisconnect(NetPacket packet)
        {
            if ((ConnectionState == ConnectionState.Connected || ConnectionState == ConnectionState.Outgoing) &&
                packet.Size >= 9 &&
                BitConverter.ToInt64(packet.RawData, 1) == ConnectTime &&
                packet.ConnectionNumber == _connectNum)
            {
                return ConnectionState == ConnectionState.Connected
                    ? DisconnectResult.Disconnect
                    : DisconnectResult.Reject;
            }
            return DisconnectResult.None;
        }

        internal void AddToReliableChannelSendQueue(BaseChannel channel)
        {
            _channelSendQueue.Enqueue(channel);
        }

        internal async Task<ShutdownResult> ShutdownAsync(ArraySegment<byte> data, bool force)
        {
            await _shutdownSemaphore.WaitAsync();
            try
            {
                //trying to shutdown already disconnected
                if (ConnectionState is ConnectionState.Disconnected or ConnectionState.ShutdownRequested)
                {
                    return ShutdownResult.None;
                }

                var result = ConnectionState == ConnectionState.Connected
                    ? ShutdownResult.WasConnected
                    : ShutdownResult.Success;

                //don't send anything
                if (force)
                {
                    ConnectionState = ConnectionState.Disconnected;
                    return result;
                }

                //reset time for reconnect protection
                Interlocked.Exchange(ref _timeSinceLastPacket, 0);

                //send shutdown packet
                _shutdownPacket = new NetPacket(PacketProperty.Disconnect, data.Count) { ConnectionNumber = _connectNum };
                FastBitConverter.GetBytes(_shutdownPacket.RawData, 1, ConnectTime);
                if (_shutdownPacket.Size >= Mtu)
                {
                    //Drop additional data
                    NetDebug.WriteError("[Peer] Disconnect additional data size more than MTU - 8!");
                }
                else if (data != null && data.Count > 0)
                {
                    data.CopyTo(_shutdownPacket.RawData, 9);
                }
                ConnectionState = ConnectionState.ShutdownRequested;
                NetDebug.Write("[Peer] Send disconnect");
                await NetManager.SendRaw(_shutdownPacket, EndPoint);
                return result;
            }
            finally { _shutdownSemaphore.Release();  }
        }

        private void UpdateRoundTripTime(int roundTripTime)
        {
            _rtt += roundTripTime;
            _rttCount++;
            RoundTripTime = _rtt/_rttCount;
            ResendDelay = 25.0 + RoundTripTime * 2.1; // 25 ms + double rtt
        }

        internal async Task AddReliablePacket(DeliveryMethod method, NetPacket p)
        {
            if (p.IsFragmented)
            {
                NetDebug.Write($"Fragment. Id: {p.FragmentId}, Part: {p.FragmentPart}, Total: {p.FragmentsTotal}");
                //Get needed array from dictionary
                ushort packetFragId = p.FragmentId;
                byte packetChannelId = p.ChannelId;
                if (!_holdedFragments.TryGetValue(packetFragId, out var incomingFragments))
                {
                    incomingFragments = new IncomingFragments
                    {
                        Fragments = new NetPacket[p.FragmentsTotal],
                        ChannelId = p.ChannelId
                    };
                    _holdedFragments.Add(packetFragId, incomingFragments);
                }

                //Cache
                var fragments = incomingFragments.Fragments;

                //Error check
                if (p.FragmentPart >= fragments.Length ||
                    fragments[p.FragmentPart] != null ||
                    p.ChannelId != incomingFragments.ChannelId)
                {
                    NetManager.PoolRecycle(p);
                    NetDebug.WriteError("Invalid fragment packet");
                    return;
                }
                //Fill array
                fragments[p.FragmentPart] = p;

                //Increase received fragments count
                incomingFragments.ReceivedCount++;

                //Increase total size
                incomingFragments.TotalSize += p.Size - NetConstants.FragmentedHeaderTotalSize;

                //Check for finish
                if (incomingFragments.ReceivedCount != fragments.Length)
                    return;

                //just simple packet
                NetPacket resultingPacket = NetManager.PoolGetPacket(incomingFragments.TotalSize);

                int pos = 0;
                for (int i = 0; i < incomingFragments.ReceivedCount; i++)
                {
                    var fragment = fragments[i];
                    int writtenSize = fragment.Size - NetConstants.FragmentedHeaderTotalSize;

                    if (pos+writtenSize > resultingPacket.RawData.Length)
                    {
                        _holdedFragments.Remove(packetFragId);
                        NetDebug.WriteError($"Fragment error pos: {pos + writtenSize} >= resultPacketSize: {resultingPacket.RawData.Length} , totalSize: {incomingFragments.TotalSize}");
                        return;
                    }
                    if (fragment.Size > fragment.RawData.Length)
                    {
                        _holdedFragments.Remove(packetFragId);
                        NetDebug.WriteError($"Fragment error size: {fragment.Size} > fragment.RawData.Length: {fragment.RawData.Length}");
                        return;
                    }

                    //Create resulting big packet
                    Buffer.BlockCopy(
                        fragment.RawData,
                        NetConstants.FragmentedHeaderTotalSize,
                        resultingPacket.RawData,
                        pos,
                        writtenSize);
                    pos += writtenSize;

                    //Free memory
                    NetManager.PoolRecycle(fragment);
                    fragments[i] = null;
                }

                //Clear memory
                _holdedFragments.Remove(packetFragId);

                //Send to process
                await NetManager.CreateReceiveEvent(resultingPacket, method, (byte)(packetChannelId / NetConstants.ChannelTypeCount), 0, this);
            }
            else //Just simple packet
            {
                await NetManager.CreateReceiveEvent(p, method, (byte)(p.ChannelId / NetConstants.ChannelTypeCount), NetConstants.ChanneledHeaderSize, this);
            }
        }

        private async Task ProcessMtuPacket(NetPacket packet)
        {
            //header + int
            if (packet.Size < NetConstants.PossibleMtu[0])
                return;

            //first stage check (mtu check and mtu ok)
            int receivedMtu = BitConverter.ToInt32(packet.RawData, 1);
            int endMtuCheck = BitConverter.ToInt32(packet.RawData, packet.Size - 4);
            if (receivedMtu != packet.Size || receivedMtu != endMtuCheck || receivedMtu > NetConstants.MaxPacketSize)
            {
                NetDebug.WriteError($"[MTU] Broken packet. RMTU {receivedMtu}, EMTU {endMtuCheck}, PSIZE {packet.Size}");
                return;
            }

            if (packet.Property == PacketProperty.MtuCheck)
            {
                _mtuCheckAttempts = 0;
                NetDebug.Write("[MTU] check. send back: " + receivedMtu);
                packet.Property = PacketProperty.MtuOk;
                await NetManager.SendRawAndRecycle(packet, EndPoint);
            }
            else if(receivedMtu > Mtu && !_finishMtu) //MtuOk
            {
                //invalid packet
                if (receivedMtu != NetConstants.PossibleMtu[_mtuIdx + 1] - NetManager.ExtraPacketSizeForLayer)
                    return;

                await _mtuMutex.WaitAsync();
                try
                {

                    SetMtu(_mtuIdx + 1);
                }
                finally { _mtuMutex.Release(); }
                //if maxed - finish.
                if (_mtuIdx == NetConstants.PossibleMtu.Length - 1)
                    _finishMtu = true;
                NetManager.PoolRecycle(packet);
                NetDebug.Write("[MTU] ok. Increase to: " + Mtu);
            }
        }

        private async Task UpdateMtuLogic(long deltaTime)
        {
            if (_finishMtu)
                return;

            _mtuCheckTimer += deltaTime;
            if (_mtuCheckTimer < MtuCheckDelay)
                return;

            _mtuCheckTimer = 0;
            _mtuCheckAttempts++;
            if (_mtuCheckAttempts >= MaxMtuCheckAttempts)
            {
                _finishMtu = true;
                return;
            }

            await _mtuMutex.WaitAsync();
            try
            {
                if (_mtuIdx >= NetConstants.PossibleMtu.Length - 1)
                    return;

                //Send increased packet
                int newMtu = NetConstants.PossibleMtu[_mtuIdx + 1] - NetManager.ExtraPacketSizeForLayer;
                var p = NetManager.PoolGetPacket(newMtu);
                p.Property = PacketProperty.MtuCheck;
                FastBitConverter.GetBytes(p.RawData, 1, newMtu);          //place into start
                FastBitConverter.GetBytes(p.RawData, p.Size - 4, newMtu); //and end of packet

                //Must check result for MTU fix
                if (await NetManager.SendRawAndRecycle(p, EndPoint) <= 0)
                    _finishMtu = true;
            }
            finally
            {
                _mtuMutex.Release();
            }
        }

        internal async Task<ConnectRequestResult> ProcessConnectRequest(NetConnectRequestPacket connRequest)
        {
            //current or new request
            switch (ConnectionState)
            {
                //P2P case
                case ConnectionState.Outgoing:
                    //fast check
                    if (connRequest.ConnectionTime < ConnectTime)
                    {
                        return ConnectRequestResult.P2PLose;
                    }
                    //slow rare case check
                    if (connRequest.ConnectionTime == ConnectTime)
                    {
                        var remoteBytes = EndPoint.Serialize();
                        var localBytes = connRequest.TargetAddress;
                        for (int i = remoteBytes.Size-1; i >= 0; i--)
                        {
                            byte rb = remoteBytes[i];
                            if (rb == localBytes[i])
                                continue;
                            if (rb < localBytes[i])
                                return ConnectRequestResult.P2PLose;
                        }
                    }
                    break;

                case ConnectionState.Connected:
                    //Old connect request
                    if (connRequest.ConnectionTime == ConnectTime)
                    {
                        //just reply accept
                        await NetManager.SendRaw(_connectAcceptPacket, EndPoint);
                    }
                    //New connect request
                    else if (connRequest.ConnectionTime > ConnectTime)
                    {
                        return ConnectRequestResult.Reconnection;
                    }
                    break;

                case ConnectionState.Disconnected:
                case ConnectionState.ShutdownRequested:
                    if (connRequest.ConnectionTime >= ConnectTime)
                        return ConnectRequestResult.NewConnection;
                    break;
            }
            return ConnectRequestResult.None;
        }

        //Process incoming packet
        internal async Task ProcessPacket(NetPacket packet)
        {
            //not initialized
            if (ConnectionState == ConnectionState.Outgoing || ConnectionState == ConnectionState.Disconnected)
            {
                NetManager.PoolRecycle(packet);
                return;
            }
            if (packet.Property == PacketProperty.ShutdownOk)
            {
                if (ConnectionState == ConnectionState.ShutdownRequested)
                    ConnectionState = ConnectionState.Disconnected;
                NetManager.PoolRecycle(packet);
                return;
            }
            if (packet.ConnectionNumber != _connectNum)
            {
                NetDebug.Write(NetLogLevel.Trace, "[RR]Old packet");
                NetManager.PoolRecycle(packet);
                return;
            }
            Interlocked.Exchange(ref _timeSinceLastPacket, 0);

            NetDebug.Write($"[RR]PacketProperty: {packet.Property}");
            switch (packet.Property)
            {
                case PacketProperty.Merged:
                    int pos = NetConstants.HeaderSize;
                    while (pos < packet.Size)
                    {
                        ushort size = BitConverter.ToUInt16(packet.RawData, pos);
                        pos += 2;
                        if (packet.RawData.Length - pos < size)
                            break;

                        NetPacket mergedPacket = NetManager.PoolGetPacket(size);
                        Buffer.BlockCopy(packet.RawData, pos, mergedPacket.RawData, 0, size);
                        mergedPacket.Size = size;

                        if (!mergedPacket.Verify())
                            break;

                        pos += size;
                        await ProcessPacket(mergedPacket);
                    }
                    NetManager.PoolRecycle(packet);
                    break;
                //If we get ping, send pong
                case PacketProperty.Ping:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _pongPacket.Sequence) > 0)
                    {
                        NetDebug.Write("[PP]Ping receive, send pong");
                        FastBitConverter.GetBytes(_pongPacket.RawData, 3, DateTime.UtcNow.Ticks);
                        _pongPacket.Sequence = packet.Sequence;
                        await NetManager.SendRaw(_pongPacket, EndPoint);
                    }
                    NetManager.PoolRecycle(packet);
                    break;

                //If we get pong, calculate ping time and rtt
                case PacketProperty.Pong:
                    if (packet.Sequence == _pingPacket.Sequence)
                    {
                        _pingTimer.Stop();
                        int elapsedMs = (int)_pingTimer.ElapsedMilliseconds;
                        RemoteTimeDelta = BitConverter.ToInt64(packet.RawData, 3) + (elapsedMs * TimeSpan.TicksPerMillisecond ) / 2 - DateTime.UtcNow.Ticks;
                        UpdateRoundTripTime(elapsedMs);
                        await NetManager.ConnectionLatencyUpdated(this, elapsedMs / 2);
                        NetDebug.Write($"[PP]Ping: {packet.Sequence} - {elapsedMs} - {RemoteTimeDelta}");
                    }
                    NetManager.PoolRecycle(packet);
                    break;

                case PacketProperty.Ack:
                case PacketProperty.Channeled:
                    if (packet.ChannelId >= _channels.Length)
                    {
                        NetManager.PoolRecycle(packet);
                        break;
                    }
                    var channel = _channels[packet.ChannelId] ?? (packet.Property == PacketProperty.Ack ? null : CreateChannel(packet.ChannelId));
                    if (channel != null)
                    {
                        if (!await channel.ProcessPacket(packet))
                            NetManager.PoolRecycle(packet);
                    }
                    break;

                //Simple packet without acks
                case PacketProperty.Unreliable:
                    await NetManager.CreateReceiveEvent(packet, DeliveryMethod.Unreliable, 0, NetConstants.HeaderSize, this);
                    return;

                case PacketProperty.MtuCheck:
                case PacketProperty.MtuOk:
                    await ProcessMtuPacket(packet);
                    break;

                default:
                    NetDebug.WriteError("Error! Unexpected packet type: " + packet.Property);
                    break;
            }
        }

        private async Task SendMerged()
        {
            if (_mergeCount == 0)
                return;
            int bytesSent;
            if (_mergeCount > 1)
            {
                NetDebug.Write("[P]Send merged: " + _mergePos + ", count: " + _mergeCount);
                bytesSent = await NetManager.SendRaw(new ArraySegment<byte>(_mergeData.RawData, 0, NetConstants.HeaderSize + _mergePos),
                    EndPoint); 
                //_mergeData.RawData, 0, NetConstants.HeaderSize + _mergePos, _remoteEndPoint);
            }
            else
            {
                //Send without length information and merging
                bytesSent = await NetManager.SendRaw(new ArraySegment<byte>(_mergeData.RawData, NetConstants.HeaderSize + 2, _mergePos - 2),
                    EndPoint);
                
                //_mergeData.RawData, NetConstants.HeaderSize + 2, _mergePos - 2, _remoteEndPoint);
            }

            if (NetManager.EnableStatistics)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(bytesSent);
            }

            _mergePos = 0;
            _mergeCount = 0;
        }

        internal async Task SendUserData(NetPacket packet)
        {
            packet.ConnectionNumber = _connectNum;
            int mergedPacketSize = NetConstants.HeaderSize + packet.Size + 2;
            const int sizeTreshold = 20;
            if (mergedPacketSize + sizeTreshold >= Mtu)
            {
                NetDebug.Write(NetLogLevel.Trace, "[P]SendingPacket: " + packet.Property);
                int bytesSent = await NetManager.SendRaw(packet, EndPoint);

                if (NetManager.EnableStatistics)
                {
                    Statistics.IncrementPacketsSent();
                    Statistics.AddBytesSent(bytesSent);
                }

                return;
            }
            if (_mergePos + mergedPacketSize > Mtu)
                await SendMerged();

            FastBitConverter.GetBytes(_mergeData.RawData, _mergePos + NetConstants.HeaderSize, (ushort)packet.Size);
            Buffer.BlockCopy(packet.RawData, 0, _mergeData.RawData, _mergePos + NetConstants.HeaderSize + 2, packet.Size);
            _mergePos += packet.Size + 2;
            _mergeCount++;
            //DebugWriteForce("Merged: " + _mergePos + "/" + (_mtu - 2) + ", count: " + _mergeCount);
        }

        internal async Task Update(long deltaTime)
        {
            Interlocked.Add(ref _timeSinceLastPacket, deltaTime);
            switch (ConnectionState)
            {
                case ConnectionState.Connected:
                    if (_timeSinceLastPacket > NetManager.DisconnectTimeout)
                    {
                        NetDebug.Write($"[UPDATE] Disconnect by timeout: {_timeSinceLastPacket} > {NetManager.DisconnectTimeout}");
                        await NetManager.ForceDisconnectPeerAsync(this, DisconnectReason.Timeout, 0, null);
                        return;
                    }
                    break;

                case ConnectionState.ShutdownRequested:
                    if (_timeSinceLastPacket > NetManager.DisconnectTimeout)
                    {
                        ConnectionState = ConnectionState.Disconnected;
                    }
                    else
                    {
                        _shutdownTimer += deltaTime;
                        if (_shutdownTimer >= ShutdownDelay)
                        {
                            _shutdownTimer = 0;
                            await NetManager.SendRaw(_shutdownPacket, EndPoint);
                        }
                    }
                    return;

                case ConnectionState.Outgoing:
                    _connectTimer += deltaTime;
                    if (_connectTimer > NetManager.ReconnectDelay)
                    {
                        _connectTimer = 0;
                        _connectAttempts++;
                        if (_connectAttempts > NetManager.MaxConnectAttempts)
                        {
                            await NetManager.ForceDisconnectPeerAsync(this, DisconnectReason.ConnectionFailed, 0, null);
                            return;
                        }

                        //else send connect again
                        await NetManager.SendRaw(_connectRequestPacket, EndPoint);
                    }
                    return;

                case ConnectionState.Disconnected:
                    return;
            }

            //Send ping
            _pingSendTimer += deltaTime;
            if (_pingSendTimer >= NetManager.PingInterval)
            {
                NetDebug.Write("[PP] Send ping...");
                //reset timer
                _pingSendTimer = 0;
                //send ping
                _pingPacket.Sequence++;
                //ping timeout
                if (_pingTimer.IsRunning)
                    UpdateRoundTripTime((int)_pingTimer.ElapsedMilliseconds);
                _pingTimer.Restart();
                await NetManager.SendRaw(_pingPacket, EndPoint);
            }

            //RTT - round trip time
            _rttResetTimer += deltaTime;
            if (_rttResetTimer >= NetManager.PingInterval * 3)
            {
                _rttResetTimer = 0;
                _rtt = RoundTripTime;
                _rttCount = 1;
            }

            await UpdateMtuLogic(deltaTime);

            //Pending send
            int count = _channelSendQueue.Count;
            while (count-- > 0)
            {
                if (!_channelSendQueue.TryDequeue(out var channel))
                    break;
                if (await channel.SendAndCheckQueue())
                {
                    // still has something to send, re-add it to the send queue
                    _channelSendQueue.Enqueue(channel);
                }
            }

            await _unreliableChannelSemaphore.WaitAsync();
            try
            {
                int unreliableCount = _unreliableChannel.Count;
                for (int i = 0; i < unreliableCount; i++)
                {
                    var packet = _unreliableChannel.Dequeue();
                    await SendUserData(packet);
                    NetManager.PoolRecycle(packet);
                }
            }
            finally { _unreliableChannelSemaphore.Release(); }

            await SendMerged();
        }

        //For reliable channel
        internal async Task RecycleAndDeliver(NetPacket packet)
        {
            if (packet.UserData != null)
            {
                if (packet.IsFragmented)
                {
                    _deliveredFragments.TryGetValue(packet.FragmentId, out ushort fragCount);
                    fragCount++;
                    if (fragCount == packet.FragmentsTotal)
                    {
                        await NetManager.MessageDelivered(this, packet.UserData);
                        _deliveredFragments.Remove(packet.FragmentId);
                    }
                    else
                    {
                        _deliveredFragments[packet.FragmentId] = fragCount;
                    }
                }
                else
                {
                    await NetManager.MessageDelivered(this, packet.UserData);
                }
                packet.UserData = null;
            }
            NetManager.PoolRecycle(packet);
        }
    }
}
