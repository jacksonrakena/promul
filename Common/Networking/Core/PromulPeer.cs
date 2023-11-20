﻿#if DEBUG
#define STATS_ENABLED
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Promul.Common.Networking.Channels;
using Promul.Common.Networking.Data;
using Promul.Common.Networking.Packets;
using Promul.Common.Networking.Packets.Internal;

namespace Promul.Common.Networking
{
    /// <summary>
    ///     The connection state of a given peer.
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

    /// <summary>
    ///     The result of a connection request.
    /// </summary>
    internal enum ConnectRequestResult
    {
        None,
        
        P2PLose,
        
        /// <summary>
        ///     The remote peer was previously connected, so this connection is a reconnection.
        /// </summary>
        Reconnection,
        
        /// <summary>
        ///     The remote peer was disconnected, so this connection is new.
        /// </summary>
        NewConnection
    }

    /// <summary>
    ///     The result of a disconnection.
    /// </summary>
    internal enum DisconnectResult
    {
        None,
        
        /// <summary>
        ///     The connection was rejected.
        /// </summary>
        Reject,
        
        /// <summary>
        ///     The connection was disconnected.
        /// </summary>
        Disconnect
    }

    internal enum ShutdownResult
    {
        None,
        Success,
        WasConnected
    }

    /// <summary>
    ///     Represents a remote peer, managed by a given <see cref="PromulManager"/> instance.
    /// </summary>
    public class PromulPeer
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
        internal volatile PromulPeer? NextPeer;
        internal PromulPeer? PrevPeer;

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
        private readonly Queue<NetworkPacket> _unreliableChannel;
        SemaphoreSlim _unreliableChannelSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<ChannelBase> _channelSendQueue;
        private readonly ChannelBase?[] _channels;

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
            public NetworkPacket[] Fragments;
            public int ReceivedCount;
            public int TotalSize;
            public byte ChannelId;
        }
        private int _fragmentId;
        private readonly Dictionary<ushort, IncomingFragments> _holdedFragments;
        private readonly Dictionary<ushort, ushort> _deliveredFragments;

        //Merging
        private readonly NetworkPacket _mergeData;
        private int _mergePos;
        private int _mergeCount;

        //Connection
        private int _connectAttempts;
        private long _connectTimer;
        private byte _connectNum;
        private NetworkPacket? _shutdownPacket;
        private const int ShutdownDelay = 300;
        private long _shutdownTimer;
        private readonly NetworkPacket _pingPacket;
        private readonly NetworkPacket _pongPacket;
        private NetworkPacket? _connectRequestPacket;
        private NetworkPacket? _connectAcceptPacket;

        /// <summary>
        ///     The remote endpoint of this peer.
        /// </summary>
        public IPEndPoint EndPoint { get; private set; }

        /// <summary>
        ///     The <see cref="PromulManager"/> instance responsible for this peer.
        /// </summary>
        public readonly PromulManager PromulManager;

        /// <summary>
        ///     The current connection state of this peer.
        /// </summary>
        public ConnectionState ConnectionState { get; private set; }

        /// <summary>
        /// Connection time for internal purposes
        /// </summary>
        internal long ConnectTime { get; private set; }

        /// <summary>
        ///     The local ID of this peer.
        /// </summary>
        public readonly int Id;

        /// <summary>
        ///     The server-assigned ID of this peer.
        /// </summary>
        public int RemoteId { get; private set; }

        /// <summary>
        ///     The current ping to this remote peer, in milliseconds.
        ///     This value is calculated by halving <see cref="RoundTripTime"/>.
        /// </summary>
        public int Ping => RoundTripTime/2;

        /// <summary>
        ///     The current time to complete a round-trip request to this remote peer, in milliseconds.
        /// </summary>
        public int RoundTripTime { get; private set; }

        /// <summary>
        ///     The current maximum transfer unit, that is, the maximum size of a given UDP packet
        ///     that will not cause fragmentation.
        /// </summary>
        public int MaximumTransferUnit { get; private set; }

        /// <summary>
        ///     The current delta between the remote peer's time and the <see cref="PromulManager"/>'s local time.
        ///     A positive value indicates the remote peer is ahead of local time.
        /// </summary>
        public long RemoteTimeDelta { get; private set; }

        /// <summary>
        ///     The time, in UTC, of the remote peer.
        /// </summary>
        public DateTime RemoteUtcTime => new DateTime(DateTime.UtcNow.Ticks + RemoteTimeDelta);

        /// <summary>
        ///     The time, in milliseconds, since the last packet was received from this peer.
        /// </summary>
        public long TimeSinceLastPacket => _timeSinceLastPacket;

        internal double ResendDelay { get; private set; } = 27.0;

        /// <summary>
        ///     The network statistics for this connection.
        /// </summary>
        public readonly NetStatistics Statistics = new NetStatistics();

        internal PromulPeer(PromulManager promulManager, IPEndPoint remoteEndPoint, int id)
        {
            Id = id;
            PromulManager = promulManager;
            ResetMtu();
            EndPoint = remoteEndPoint;
            ConnectionState = ConnectionState.Connected;
            _mergeData = NetworkPacket.FromProperty(PacketProperty.Merged, NetConstants.MaxPacketSize);
            _pongPacket = NetworkPacket.FromProperty(PacketProperty.Pong, 0);
            _pingPacket = NetworkPacket.FromProperty(PacketProperty.Ping, 0);
            _pingPacket.Sequence = 1;

            _unreliableChannel = new Queue<NetworkPacket>();
            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
            _deliveredFragments = new Dictionary<ushort, ushort>();

            _channels = new ChannelBase[promulManager.ChannelsCount * NetConstants.ChannelTypeCount];
            _channelSendQueue = new ConcurrentQueue<ChannelBase>();
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
            if (PromulManager.MtuOverride > 0)
                OverrideMtu(PromulManager.MtuOverride);
            else if (PromulManager.UseSafeMtu)
                SetMtu(0);
            else
                SetMtu(1);
        }

        private void SetMtu(int mtuIdx)
        {
            _mtuIdx = mtuIdx;
            MaximumTransferUnit = NetConstants.PossibleMtu[mtuIdx] - PromulManager.ExtraPacketSizeForLayer;
        }

        private void OverrideMtu(int mtuValue)
        {
            MaximumTransferUnit = mtuValue;
            _finishMtu = true;
        }

        /// <summary>
        ///     Returns the number of packets in queue for sending in the given reliable channel.
        /// </summary>
        /// <param name="channelNumber">The number of the channel to query.</param>
        /// <param name="ordered">If true, this method will query the reliable-ordered channel, otherwise, the reliable-unordered channel.</param>
        /// <returns>The number of packets remaining in the given queue.</returns>
        public int GetRemainingReliableQueuePacketCount(byte channelNumber, bool ordered)
        {
            int idx = channelNumber * NetConstants.ChannelTypeCount +
                       (byte) (ordered ? DeliveryMethod.ReliableOrdered : DeliveryMethod.ReliableUnordered);
            var channel = _channels[idx];
            return channel != null ? ((ReliableChannel)channel).PacketsInQueue : 0;
        }
        
        private ChannelBase CreateChannel(byte idx)
        {
            ChannelBase newChannel = _channels[idx];
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
            ChannelBase prevChannel = Interlocked.CompareExchange(ref _channels[idx], newChannel, null);
            if (prevChannel != null)
                return prevChannel;

            return newChannel;
        }

        internal static async Task<PromulPeer> ConnectToAsync(PromulManager manager, IPEndPoint remote, int id, byte connectionNumber, ArraySegment<byte> data)
        {
            var time = DateTime.UtcNow.Ticks;
            var packet = NetConnectRequestPacket.Make(data, remote.Serialize(), time, id);
            packet.ConnectionNumber = connectionNumber;
            var peer = new PromulPeer(manager, remote, id, time, connectionNumber)
            {
                ConnectionState = ConnectionState.Outgoing,
                _connectRequestPacket = packet
            };

            await manager.SendRaw(peer._connectRequestPacket, peer.EndPoint);

            NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {peer.ConnectTime}");
            return peer;
        }
        
        internal static async Task<PromulPeer> AcceptAsync(PromulManager promulManager, ConnectionRequest request, int id)
        {
            var peer = new PromulPeer(promulManager, request.RemoteEndPoint, id, request.InternalPacket.ConnectionTime, request.InternalPacket.ConnectionNumber)
            {
                RemoteId = request.InternalPacket.PeerId,
                _connectAcceptPacket = NetConnectAcceptPacket.Make(request.InternalPacket.ConnectionTime, request.InternalPacket.ConnectionNumber, id),
                ConnectionState = ConnectionState.Connected
            };
            await promulManager.SendRaw(peer._connectAcceptPacket, peer.EndPoint);

            NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {peer.ConnectTime}");
            return peer;
        }

        private PromulPeer(PromulManager promulManager, IPEndPoint remote, int id,
            long connectTime, byte connectionNumber)
        {
            Id = id;
            Statistics = new NetStatistics();
            PromulManager = promulManager;
            ResetMtu();
            EndPoint = remote;
            ConnectionState = ConnectionState.Connected;
            _mergeData = NetworkPacket.FromProperty(PacketProperty.Merged, NetConstants.MaxPacketSize);
            _pongPacket = NetworkPacket.FromProperty(PacketProperty.Pong, 0);
            _pingPacket = NetworkPacket.FromProperty(PacketProperty.Ping, 0);
            _pingPacket.Sequence = 1;

            _unreliableChannel = new Queue<NetworkPacket>();
            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
            _deliveredFragments = new Dictionary<ushort, ushort>();

            _channels = new ChannelBase[promulManager.ChannelsCount * NetConstants.ChannelTypeCount];
            _channelSendQueue = new ConcurrentQueue<ChannelBase>();

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
        ///     Gets the maximum size of user-provided data that can be sent without fragmentation.
        ///     This method subtracts the size of the relevant packet headers.
        /// </summary>
        /// <param name="options">The type of packet to be calculated.</param>
        /// <returns>The maximum transmission unit size, in bytes, for the queried packet type.</returns>
        public int GetUserMaximumTransmissionUnit(DeliveryMethod options)
        {
            return MaximumTransferUnit - NetworkPacket.GetHeaderSize(options == DeliveryMethod.Unreliable ? PacketProperty.Unreliable : PacketProperty.Channeled);
        }

        /// <summary>
        ///     Sends a data stream to the remote peer. This method will queue the data in the correct
        ///     delivery channel, so completion of this method does NOT indicate completion of the
        ///     sending process.
        /// </summary>
        /// <param name="data">The data to transmit.</param>
        /// <param name="channelNumber">The number of channel to send on.</param>
        /// <param name="deliveryMethod">The delivery method to send the data.</param>
        /// <exception cref="TooBigPacketException">
        ///     Thrown in the following instances:<br />
        ///     - The size of <see cref="data"/> exceeds <see cref="GetUserMaximumTransmissionUnit"/> if <see cref="DeliveryMethod"/> is <see cref="DeliveryMethod.Unreliable"/>.<br />
        ///     - The number of computed fragments exceeds <see cref="ushort.MaxValue"/>.
        /// </exception>
        public void Send(ArraySegment<byte> data, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered, byte channelNumber = 0)
        {
            SendInternal(data, channelNumber, deliveryMethod);
        }

        /// <summary>
        ///     Sends a data stream to the remote peer. This method will queue the data in the correct
        ///     delivery channel, so completion of this method does NOT indicate completion of the
        ///     sending process.
        /// </summary>
        /// <param name="writer">The data to transmit.</param>
        /// <param name="channelNumber">The number of channel to send on.</param>
        /// <param name="deliveryMethod">The delivery method to send the data.</param>
        /// <exception cref="TooBigPacketException">
        ///     Thrown in the following instances:<br />
        ///     - The size of <see cref="writer"/> exceeds <see cref="GetUserMaximumTransmissionUnit"/> if <see cref="DeliveryMethod"/> is <see cref="DeliveryMethod.Unreliable"/>.<br />
        ///     - The number of computed fragments exceeds <see cref="ushort.MaxValue"/>.
        /// </exception>
        public void Send(BinaryWriter writer, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered,
            byte channelNumber = 0)
        {
            Send(new BinaryReader(writer.BaseStream).ReadBytes(int.MaxValue), deliveryMethod, channelNumber);
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
            ChannelBase?  channel = null;

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
            int headerSize = NetworkPacket.GetHeaderSize(property);
            //Save mtu for multithread
            int mtu = MaximumTransferUnit;
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

                    NetworkPacket p = NetworkPacket.Empty(headerSize + sendLength + NetConstants.FragmentHeaderSize);
                    p.Property = property;
                    p.FragmentId = currentFragmentId;
                    p.FragmentPart = partIdx;
                    p.FragmentsTotal = (ushort)totalPackets;
                    p.MarkFragmented();

                    if (data.Array != null) Buffer.BlockCopy(data.Array, data.Offset + partIdx * packetDataSize, p.Data.Array,p.Data.Offset+NetConstants.FragmentedHeaderTotalSize, sendLength);
                    channel?.EnqueuePacket(p);

                    length -= sendLength;
                }
                return;
            }

            //Else just send
            NetworkPacket packet = NetworkPacket.Empty(headerSize + length);
            packet.Property = property;
            if (data.Array != null) Buffer.BlockCopy(data.Array, data.Offset, 
                packet.Data.Array, packet.Data.Offset+headerSize, length);

            if (channel == null) //unreliable
            {
                _unreliableChannelSemaphore.Wait();
                _unreliableChannel.Enqueue(packet);
                _unreliableChannelSemaphore.Release();
            }
            else
            {
                channel.EnqueuePacket(packet);
            }
        }

        internal DisconnectResult ProcessDisconnect(NetworkPacket packet)
        {
            if ((ConnectionState == ConnectionState.Connected || ConnectionState == ConnectionState.Outgoing) &&
                packet.Data.Count >= 9 &&
                BitConverter.ToInt64(packet.Data[1..]) == ConnectTime &&
                packet.ConnectionNumber == _connectNum)
            {
                return ConnectionState == ConnectionState.Connected
                    ? DisconnectResult.Disconnect
                    : DisconnectResult.Reject;
            }
            return DisconnectResult.None;
        }

        internal void AddToReliableChannelSendQueue(ChannelBase channel)
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
                _shutdownPacket = NetworkPacket.FromProperty(PacketProperty.Disconnect, data.Count);
                _shutdownPacket.ConnectionNumber = _connectNum;
                FastBitConverter.GetBytes(_shutdownPacket.Data.Array, _shutdownPacket.Data.Offset+1, ConnectTime);
                if (_shutdownPacket.Data.Count >= MaximumTransferUnit)
                {
                    //Drop additional data
                    NetDebug.WriteError("[Peer] Disconnect additional data size more than MTU - 8!");
                }
                else if (data != null && data.Count > 0)
                {
                    data.CopyTo(_shutdownPacket.Data.Array,_shutdownPacket.Data.Offset+9);
                }
                ConnectionState = ConnectionState.ShutdownRequested;
                NetDebug.Write("[Peer] Send disconnect");
                await PromulManager.SendRaw(_shutdownPacket, EndPoint);
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

        internal async Task AddReliablePacket(DeliveryMethod method, NetworkPacket p)
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
                        Fragments = new NetworkPacket[p.FragmentsTotal],
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
                    //NetManager.PoolRecycle(p);
                    NetDebug.WriteError("Invalid fragment packet");
                    return;
                }
                //Fill array
                fragments[p.FragmentPart] = p;

                //Increase received fragments count
                incomingFragments.ReceivedCount++;

                //Increase total size
                incomingFragments.TotalSize += p.Data.Count - NetConstants.FragmentedHeaderTotalSize;

                //Check for finish
                if (incomingFragments.ReceivedCount != fragments.Length)
                    return;

                //just simple packet
                NetworkPacket resultingPacket = NetworkPacket.Empty(incomingFragments.TotalSize);

                int pos = 0;
                for (int i = 0; i < incomingFragments.ReceivedCount; i++)
                {
                    var fragment = fragments[i];
                    int writtenSize = fragment.Data.Count - NetConstants.FragmentedHeaderTotalSize;

                    if (pos+writtenSize > resultingPacket.Data.Count)
                    {
                        _holdedFragments.Remove(packetFragId);
                        NetDebug.WriteError($"Fragment error pos: {pos + writtenSize} >= resultPacketSize: {resultingPacket.Data.Count} , totalSize: {incomingFragments.TotalSize}");
                        return;
                    }
                    if (fragment.Data.Count > fragment.Data.Count)
                    {
                        _holdedFragments.Remove(packetFragId);
                        NetDebug.WriteError($"Fragment error size: {fragment.Data.Count} > fragment.RawData.Length: {fragment.Data.Count}");
                        return;
                    }

                    //Create resulting big packet
                    Buffer.BlockCopy(
                        fragment.Data.Array,
                        fragment.Data.Offset+NetConstants.FragmentedHeaderTotalSize,
                        resultingPacket.Data.Array,
                        resultingPacket.Data.Offset+pos,
                        writtenSize);
                    pos += writtenSize;

                    //Free memory
                    //NetManager.PoolRecycle(fragment);
                    fragments[i] = null;
                }

                //Clear memory
                _holdedFragments.Remove(packetFragId);

                //Send to process
                await PromulManager.CreateReceiveEvent(resultingPacket, method, (byte)(packetChannelId / NetConstants.ChannelTypeCount), 0, this);
            }
            else //Just simple packet
            {
                await PromulManager.CreateReceiveEvent(p, method, (byte)(p.ChannelId / NetConstants.ChannelTypeCount), NetConstants.ChanneledHeaderSize, this);
            }
        }

        private async Task ProcessMtuPacket(NetworkPacket packet)
        {
            //header + int
            if (packet.Data.Count < NetConstants.PossibleMtu[0])
                return;

            //first stage check (mtu check and mtu ok)
            int receivedMtu = BitConverter.ToInt32(packet.Data.Array, packet.Data.Offset+1);
            int endMtuCheck = BitConverter.ToInt32(packet.Data.Array, packet.Data.Offset+packet.Data.Count - 4);
            if (receivedMtu != packet.Data.Count || receivedMtu != endMtuCheck || receivedMtu > NetConstants.MaxPacketSize)
            {
                NetDebug.WriteError($"[MTU] Broken packet. RMTU {receivedMtu}, EMTU {endMtuCheck}, PSIZE {packet.Data.Count}");
                return;
            }

            if (packet.Property == PacketProperty.MtuCheck)
            {
                _mtuCheckAttempts = 0;
                NetDebug.Write("[MTU] check. send back: " + receivedMtu);
                packet.Property = PacketProperty.MtuOk;
                await PromulManager.SendRaw(packet, EndPoint);
            }
            else if(receivedMtu > MaximumTransferUnit && !_finishMtu) //MtuOk
            {
                //invalid packet
                if (receivedMtu != NetConstants.PossibleMtu[_mtuIdx + 1] - PromulManager.ExtraPacketSizeForLayer)
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
                //NetManager.PoolRecycle(packet);
                NetDebug.Write("[MTU] ok. Increase to: " + MaximumTransferUnit);
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
                int newMtu = NetConstants.PossibleMtu[_mtuIdx + 1] - PromulManager.ExtraPacketSizeForLayer;
                var p = NetworkPacket.Empty(newMtu);// NetManager.PoolGetPacket(newMtu);
                p.Property = PacketProperty.MtuCheck;
                FastBitConverter.GetBytes(p.Data.Array, p.Data.Offset+1, newMtu);          //place into start
                FastBitConverter.GetBytes(p.Data.Array, p.Data.Offset+p.Data.Count - 4, newMtu); //and end of packet

                //Must check result for MTU fix
                if (await PromulManager.SendRaw(p, EndPoint) <= 0)
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
                        await PromulManager.SendRaw(_connectAcceptPacket, EndPoint);
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
        internal async Task ProcessPacket(NetworkPacket packet)
        {
            //not initialized
            if (ConnectionState == ConnectionState.Outgoing || ConnectionState == ConnectionState.Disconnected)
            {
                //NetManager.PoolRecycle(packet);
                return;
            }
            if (packet.Property == PacketProperty.ShutdownOk)
            {
                if (ConnectionState == ConnectionState.ShutdownRequested)
                    ConnectionState = ConnectionState.Disconnected;
                //NetManager.PoolRecycle(packet);
                return;
            }
            if (packet.ConnectionNumber != _connectNum)
            {
                NetDebug.Write(NetLogLevel.Trace, "[RR]Old packet");
                //NetManager.PoolRecycle(packet);
                return;
            }
            Interlocked.Exchange(ref _timeSinceLastPacket, 0);

            NetDebug.Write($"[RR]PacketProperty: {packet.Property}");
            switch (packet.Property)
            {
                case PacketProperty.Merged:
                    int pos = NetConstants.HeaderSize;
                    while (pos < packet.Data.Count)
                    {
                        ushort size = BitConverter.ToUInt16(packet.Data.Array, packet.Data.Offset+pos);
                        pos += 2;
                        if (packet.Data.Count - pos < size)
                            break;

                        NetworkPacket mergedPacket = NetworkPacket.Empty(size);
                        Buffer.BlockCopy(packet.Data.Array, packet.Data.Offset+pos, mergedPacket.Data.Array, mergedPacket.Data.Offset, size);

                        if (!mergedPacket.Verify())
                            break;

                        pos += size;
                        await ProcessPacket(mergedPacket);
                    }
                    //NetManager.PoolRecycle(packet);
                    break;
                //If we get ping, send pong
                case PacketProperty.Ping:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _pongPacket.Sequence) > 0)
                    {
                        NetDebug.Write("[PP]Ping receive, send pong");
                        FastBitConverter.GetBytes(_pongPacket.Data.Array, _pongPacket.Data.Offset+3, DateTime.UtcNow.Ticks);
                        _pongPacket.Sequence = packet.Sequence;
                        await PromulManager.SendRaw(_pongPacket, EndPoint);
                    }
                    //NetManager.PoolRecycle(packet);
                    break;

                //If we get pong, calculate ping time and rtt
                case PacketProperty.Pong:
                    if (packet.Sequence == _pingPacket.Sequence)
                    {
                        _pingTimer.Stop();
                        int elapsedMs = (int)_pingTimer.ElapsedMilliseconds;
                        RemoteTimeDelta = BitConverter.ToInt64(packet.Data[3..]) + (elapsedMs * TimeSpan.TicksPerMillisecond ) / 2 - DateTime.UtcNow.Ticks;
                        UpdateRoundTripTime(elapsedMs);
                        await PromulManager.ConnectionLatencyUpdated(this, elapsedMs / 2);
                        NetDebug.Write($"[PP]Ping: {packet.Sequence} - {elapsedMs} - {RemoteTimeDelta}");
                    }
                   // NetManager.PoolRecycle(packet);
                    break;

                case PacketProperty.Ack:
                case PacketProperty.Channeled:
                    if (packet.ChannelId >= _channels.Length)
                    {
                        //NetManager.PoolRecycle(packet);
                        break;
                    }
                    var channel = _channels[packet.ChannelId] ?? (packet.Property == PacketProperty.Ack ? null : CreateChannel(packet.ChannelId));
                    if (channel != null)
                    {
                        if (!await channel.HandlePacketAsync(packet)) {}
                            //NetManager.PoolRecycle(packet);
                    }
                    break;

                //Simple packet without acks
                case PacketProperty.Unreliable:
                    await PromulManager.CreateReceiveEvent(packet, DeliveryMethod.Unreliable, 0, NetConstants.HeaderSize, this);
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
                bytesSent = await PromulManager.SendRaw(new ArraySegment<byte>(_mergeData.Data.Array, _mergeData.Data.Offset, NetConstants.HeaderSize + _mergePos),
                    EndPoint); 
                //_mergeData.RawData, 0, NetConstants.HeaderSize + _mergePos, _remoteEndPoint);
            }
            else
            {
                //Send without length information and merging
                bytesSent = await PromulManager.SendRaw(new ArraySegment<byte>(_mergeData.Data.Array, _mergeData.Data.Offset+NetConstants.HeaderSize + 2, _mergePos - 2),
                    EndPoint);
                
                //_mergeData.RawData, NetConstants.HeaderSize + 2, _mergePos - 2, _remoteEndPoint);
            }

            if (PromulManager.RecordNetworkStatistics)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(bytesSent);
            }

            _mergePos = 0;
            _mergeCount = 0;
        }

        internal async Task SendUserData(NetworkPacket packet)
        {
            packet.ConnectionNumber = _connectNum;
            int mergedPacketSize = NetConstants.HeaderSize + packet.Data.Count + 2;
            const int sizeTreshold = 20;
            if (mergedPacketSize + sizeTreshold >= MaximumTransferUnit)
            {
                NetDebug.Write(NetLogLevel.Trace, "[P]SendingPacket: " + packet.Property);
                int bytesSent = await PromulManager.SendRaw(packet, EndPoint);

                if (PromulManager.RecordNetworkStatistics)
                {
                    Statistics.IncrementPacketsSent();
                    Statistics.AddBytesSent(bytesSent);
                }

                return;
            }
            if (_mergePos + mergedPacketSize > MaximumTransferUnit)
                await SendMerged();

            FastBitConverter.GetBytes(_mergeData.Data.Array, _mergeData.Data.Offset+_mergePos + NetConstants.HeaderSize, (ushort)packet.Data.Count);
            packet.Data.CopyTo(_mergeData.Data.Array, _mergeData.Data.Offset+_mergePos+NetConstants.HeaderSize+2);
            //Buffer.BlockCopy(packet.RawData, 0, _mergeData.RawData, _mergePos + NetConstants.HeaderSize + 2, packet.Size);
            _mergePos += packet.Data.Count + 2;
            _mergeCount++;
            //DebugWriteForce("Merged: " + _mergePos + "/" + (_mtu - 2) + ", count: " + _mergeCount);
        }

        internal async Task Update(long deltaTime)
        {
            Interlocked.Add(ref _timeSinceLastPacket, deltaTime);
            switch (ConnectionState)
            {
                case ConnectionState.Connected:
                    if (_timeSinceLastPacket > PromulManager.DisconnectTimeout)
                    {
                        NetDebug.Write($"[UPDATE] Disconnect by timeout: {_timeSinceLastPacket} > {PromulManager.DisconnectTimeout}");
                        await PromulManager.ForceDisconnectPeerAsync(this, DisconnectReason.Timeout, 0, null);
                        return;
                    }
                    break;

                case ConnectionState.ShutdownRequested:
                    if (_timeSinceLastPacket > PromulManager.DisconnectTimeout)
                    {
                        ConnectionState = ConnectionState.Disconnected;
                    }
                    else
                    {
                        _shutdownTimer += deltaTime;
                        if (_shutdownTimer >= ShutdownDelay)
                        {
                            _shutdownTimer = 0;
                            await PromulManager.SendRaw(_shutdownPacket, EndPoint);
                        }
                    }
                    return;

                case ConnectionState.Outgoing:
                    _connectTimer += deltaTime;
                    if (_connectTimer > PromulManager.ReconnectDelay)
                    {
                        _connectTimer = 0;
                        _connectAttempts++;
                        if (_connectAttempts > PromulManager.MaximumConnectionAttempts)
                        {
                            await PromulManager.ForceDisconnectPeerAsync(this, DisconnectReason.ConnectionFailed, 0, null);
                            return;
                        }

                        //else send connect again
                        await PromulManager.SendRaw(_connectRequestPacket, EndPoint);
                    }
                    return;

                case ConnectionState.Disconnected:
                    return;
            }

            //Send ping
            _pingSendTimer += deltaTime;
            if (_pingSendTimer >= PromulManager.PingInterval)
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
                await PromulManager.SendRaw(_pingPacket, EndPoint);
            }

            //RTT - round trip time
            _rttResetTimer += deltaTime;
            if (_rttResetTimer >= PromulManager.PingInterval * 3)
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
                if (await channel.UpdateQueueAsync())
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
                    //NetManager.PoolRecycle(packet);
                }
            }
            finally { _unreliableChannelSemaphore.Release(); }

            await SendMerged();
        }

        //For reliable channel
        internal async Task RecycleAndDeliver(NetworkPacket packet)
        {
            //if (packet.UserData != null)
            //{
                if (packet.IsFragmented)
                {
                    _deliveredFragments.TryGetValue(packet.FragmentId, out ushort fragCount);
                    fragCount++;
                    if (fragCount == packet.FragmentsTotal)
                    {
                        // TODO FIX THIS
                        await PromulManager.MessageDelivered(this, null);
                        _deliveredFragments.Remove(packet.FragmentId);
                    }
                    else
                    {
                        _deliveredFragments[packet.FragmentId] = fragCount;
                    }
                }
                else
                {
                    // TODO FIX THIS
                    await PromulManager.MessageDelivered(this, null);
                }
                //packet.UserData = null;
            //}
            //NetManager.PoolRecycle(packet);
        }
    }
}