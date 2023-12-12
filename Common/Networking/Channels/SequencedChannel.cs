using System;
using System.Threading;
using System.Threading.Tasks;
using Promul.Common.Networking.Packets;

namespace Promul.Common.Networking.Channels
{
    internal sealed class SequencedChannel : ChannelBase
    {
        private NetworkPacket? _ackPacket;
        private readonly byte _id;

        private readonly SemaphoreSlim _outgoingQueueSem = new(1, 1);
        private readonly bool _reliable;
        private NetworkPacket? _lastPacket;
        private long _lastPacketSendTime;
        private int _localSequence;
        private bool _mustSendAck;
        private ushort _remoteSequence;

        public SequencedChannel(PeerBase peer, bool reliable, byte id) : base(peer)
        {
            _id = id;
            _reliable = reliable;
            if (_reliable)
            {
                var ap = NetworkPacket.FromProperty(PacketProperty.Ack, 0);
                ap.ChannelId = id;
                _ackPacket = ap;
            }
        }

        protected override async Task<bool> FlushQueueAsync()
        {
            if (_reliable && OutgoingQueue.Count == 0)
            {
                var currentTime = DateTime.UtcNow.Ticks;
                var packetHoldTime = currentTime - _lastPacketSendTime;
                if (packetHoldTime >= Peer.ResendDelay * TimeSpan.TicksPerMillisecond)
                {
                    var packet = _lastPacket;
                    if (packet != null)
                    {
                        _lastPacketSendTime = currentTime;
                        await Peer.SendUserData(packet.Value);
                    }
                }
            }
            else
            {
                await _outgoingQueueSem.WaitAsync();

                while (OutgoingQueue.Count > 0)
                {
                    var packet = OutgoingQueue.Dequeue();
                    _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                    packet.Sequence = (ushort)_localSequence;
                    packet.ChannelId = _id;
                    await Peer.SendUserData(packet);

                    if (_reliable && OutgoingQueue.Count == 0)
                    {
                        _lastPacketSendTime = DateTime.UtcNow.Ticks;
                        _lastPacket = packet;
                    }
                    //Peer.NetManager.PoolRecycle(packet);
                }

                _outgoingQueueSem.Release();
            }

            if (_reliable && _mustSendAck)
            {
                _mustSendAck = false;
                var p = _ackPacket.Value;
                p.Sequence = _remoteSequence;
                _ackPacket = p;
                await Peer.SendUserData(_ackPacket.Value);
            }

            return _lastPacket != null;
        }

        public override async ValueTask<bool> HandlePacketAsync(NetworkPacket packet)
        {
            if (packet.IsFragmented)
                return false;
            if (packet.Property == PacketProperty.Ack)
            {
                if (_reliable && _lastPacket != null && packet.Sequence == _lastPacket.Value.Sequence)
                    _lastPacket = null;
                return false;
            }

            var relative = NetUtils.RelativeSequenceNumber(packet.Sequence, _remoteSequence);
            var packetProcessed = false;
            if (packet.Sequence < NetConstants.MaxSequence && relative > 0)
            {
                if (Peer.PromulManager.RecordNetworkStatistics)
                {
                    Peer.Statistics.AddPacketLoss(relative - 1);
                    Peer.PromulManager.Statistics.AddPacketLoss(relative - 1);
                }

                _remoteSequence = packet.Sequence;
                await Peer.PromulManager.CreateReceiveEvent(
                    packet,
                    _reliable ? DeliveryMethod.ReliableSequenced : DeliveryMethod.Sequenced,
                    (byte)(packet.ChannelId / NetConstants.ChannelTypeCount),
                    NetConstants.ChanneledHeaderSize,
                    Peer);
                packetProcessed = true;
            }

            if (_reliable)
            {
                _mustSendAck = true;
                AddToPeerChannelSendQueue();
            }

            return packetProcessed;
        }
    }
}