using System;
using System.Threading;
using System.Threading.Tasks;
using Promul.Common.Networking.Packets;

namespace Promul.Common.Networking.Channels
{
    internal sealed class SequencedChannel : ChannelBase
    {
        private int _localSequence;
        private ushort _remoteSequence;
        private readonly bool _reliable;
        private NetworkPacket? _lastPacket;
        private readonly NetworkPacket? _ackPacket;
        private bool _mustSendAck;
        private readonly byte _id;
        private long _lastPacketSendTime;

        public SequencedChannel(PromulPeer peer, bool reliable, byte id) : base(peer)
        {
            _id = id;
            _reliable = reliable;
            if (_reliable)
            {
                _ackPacket = NetworkPacket.FromProperty(PacketProperty.Ack, 0);
                _ackPacket.ChannelId = id;
            }
        }

        private readonly SemaphoreSlim _outgoingQueueSem = new SemaphoreSlim(1, 1);
        protected override async Task<bool> FlushQueueAsync()
        {
            if (_reliable && OutgoingQueue.Count == 0)
            {
                long currentTime = DateTime.UtcNow.Ticks;
                long packetHoldTime = currentTime - _lastPacketSendTime;
                if (packetHoldTime >= Peer.ResendDelay * TimeSpan.TicksPerMillisecond)
                {
                    var packet = _lastPacket;
                    if (packet != null)
                    {
                        _lastPacketSendTime = currentTime;
                        await Peer.SendUserData(packet);
                    }
                }
            }
            else
            {
                await _outgoingQueueSem.WaitAsync();

                while (OutgoingQueue.Count > 0)
                {
                    NetworkPacket packet = OutgoingQueue.Dequeue();
                    _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                    packet.Sequence = (ushort)_localSequence;
                    packet.ChannelId = _id;
                    await Peer.SendUserData(packet);

                    if (_reliable && OutgoingQueue.Count == 0)
                    {
                        _lastPacketSendTime = DateTime.UtcNow.Ticks;
                        _lastPacket = packet;
                    }
                    else
                    {
                        //Peer.NetManager.PoolRecycle(packet);
                    }
                }
                
                _outgoingQueueSem.Release();
            }

            if (_reliable && _mustSendAck)
            {
                _mustSendAck = false;
                _ackPacket!.Sequence = _remoteSequence;
                await Peer.SendUserData(_ackPacket);
            }

            return _lastPacket != null;
        }

        public override async Task<bool> HandlePacketAsync(NetworkPacket packet)
        {
            if (packet.IsFragmented)
                return false;
            if (packet.Property == PacketProperty.Ack)
            {
                if (_reliable && _lastPacket != null && packet.Sequence == _lastPacket.Sequence)
                    _lastPacket = null;
                return false;
            }
            int relative = NetUtils.RelativeSequenceNumber(packet.Sequence, _remoteSequence);
            bool packetProcessed = false;
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
