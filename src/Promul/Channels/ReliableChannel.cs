using System;
using System.Threading;
using System.Threading.Tasks;
namespace Promul
{
    internal sealed class ReliableChannel : ChannelBase
    {
        private const int BitsInByte = 8;

        private readonly DeliveryMethod _deliveryMethod;
        private readonly bool[] _earlyReceived; //for unordered
        private readonly byte _id;
        private readonly bool _ordered;

        private NetworkPacket _outgoingAcks; //for send acks
        private readonly SemaphoreSlim _outgoingAcksSem = new(1, 1);
        private readonly PendingReliablePacket?[] _pendingPackets; //for unacked packets and duplicates

        private readonly SemaphoreSlim _pendingPacketsSem = new(1, 1);

        private readonly NetworkPacket?[]? _receivedPackets; //for order
        private readonly int _windowSize;

        private int _localSequence;
        private int _localWindowStart;

        private bool _mustSendAcks;
        private int _remoteSequence;
        private int _remoteWindowStart;

        public ReliableChannel(PeerBase peer, bool ordered, byte id) : base(peer)
        {
            _id = id;
            _windowSize = NetConstants.DefaultWindowSize;
            _ordered = ordered;
            _pendingPackets = new PendingReliablePacket?[_windowSize];

            if (_ordered)
            {
                _deliveryMethod = DeliveryMethod.ReliableOrdered;
                _receivedPackets = new NetworkPacket?[_windowSize];
                _earlyReceived = Array.Empty<bool>();
            }
            else
            {
                _deliveryMethod = DeliveryMethod.ReliableUnordered;
                _earlyReceived = new bool[_windowSize];
            }

            _localWindowStart = 0;
            _localSequence = 0;
            _remoteSequence = 0;
            _remoteWindowStart = 0;
            _outgoingAcks = NetworkPacket.FromProperty(PacketProperty.Ack, (_windowSize - 1) / BitsInByte + 2);
            _outgoingAcks.ChannelId = id;
        }

        private async ValueTask ProcessAckAsync(NetworkPacket packet)
        {
            if (packet.Data.Count != _outgoingAcks.Data.Count)
            {
                Peer.LogDebug("[PA]Invalid acks packet size");
                return;
            }

            var ackWindowStart = packet.Sequence;
            var windowRel = NetUtils.RelativeSequenceNumber(_localWindowStart, ackWindowStart);
            if (ackWindowStart >= NetConstants.MaxSequence || windowRel < 0)
            {
                Peer.LogDebug("[PA]Bad window start");
                return;
            }

            //check relevance
            if (windowRel >= _windowSize)
            {
                Peer.LogDebug("[PA]Old acks");
                return;
            }

            await _pendingPacketsSem.WaitAsync();
            try
            {
                for (var pendingSeq = _localWindowStart;
                     pendingSeq != _localSequence;
                     pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    var rel = NetUtils.RelativeSequenceNumber(pendingSeq, ackWindowStart);
                    if (rel >= _windowSize) break;

                    var pendingIdx = pendingSeq % _windowSize;
                    var currentByte = NetConstants.ChanneledHeaderSize + pendingIdx / BitsInByte;
                    var currentBit = pendingIdx % BitsInByte;
                    if ((packet.Data[currentByte] & (1 << currentBit)) == 0)
                    {
                        if (Peer.PromulManager.RecordNetworkStatistics)
                        {
                            Peer.Statistics.IncrementPacketLoss();
                            Peer.PromulManager.Statistics.IncrementPacketLoss();
                        }

                        //Skip false ack
                        Peer.LogDebug($"[Packet {packet.Sequence}] False ack for {pendingSeq}");
                        continue;
                    }

                    if (pendingSeq == _localWindowStart)
                        //Move window
                        _localWindowStart = (_localWindowStart + 1) % NetConstants.MaxSequence;

                    //clear packet
                    var p = _pendingPackets[pendingIdx];
                    if (p != null)
                    {
                        await p.ClearAsync(Peer);
                        _pendingPackets[pendingIdx] = null;
                    }
                }
            }
            finally
            {
                _pendingPacketsSem.Release();
            }
        }

        protected override async Task<bool> FlushQueueAsync()
        {
            if (_mustSendAcks)
            {
                _mustSendAcks = false;
                await _outgoingAcksSem.WaitAsync();
                try
                {
                    await Peer.SendUserData(_outgoingAcks);
                }
                finally
                {
                    _outgoingAcksSem.Release();
                }
            }

            var currentTime = DateTime.UtcNow.Ticks;
            var hasPendingPackets = false;

            await _pendingPacketsSem.WaitAsync();
            try
            {
                await outgoingQueueSem.WaitAsync();
                try 
                {
                    while (OutgoingQueue.Count > 0)
                    {
                        var relate = NetUtils.RelativeSequenceNumber(_localSequence, _localWindowStart);
                        if (relate >= _windowSize)
                            break;

                        var netPacket = OutgoingQueue.Dequeue();
                        netPacket.Sequence = (ushort)_localSequence;
                        netPacket.ChannelId = _id;
                        var prp = new PendingReliablePacket(netPacket);
                        _pendingPackets[_localSequence % _windowSize] = prp;
                        //_pendingPackets[_localSequence % _windowSize] = new PendingReliablePacket(); .Init(netPacket);
                        _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                finally
                {
                    outgoingQueueSem.Release();
                }

                //send
                for (var pendingSeq = _localWindowStart;
                     pendingSeq != _localSequence;
                     pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    var p = _pendingPackets[pendingSeq % _windowSize];
                    if (p != null)
                    {
                        await p.TrySendAsync(currentTime, Peer);
                        _pendingPackets[pendingSeq % _windowSize] = p;
                        hasPendingPackets = true;
                    }
                }
            }
            finally
            {
                _pendingPacketsSem.Release();
            }

            return hasPendingPackets || _mustSendAcks || OutgoingQueue.Count > 0;
        }


        //Process incoming packet
        public override async ValueTask<bool> HandlePacketAsync(NetworkPacket packet)
        {
            if (packet.Property == PacketProperty.Ack)
            {
                await ProcessAckAsync(packet);
                return false;
            }

            int seq = packet.Sequence;
            if (seq >= NetConstants.MaxSequence)
            {
                Peer.LogDebug("[RR]Bad sequence");
                return false;
            }

            var relate = NetUtils.RelativeSequenceNumber(seq, _remoteWindowStart);
            var relateSeq = NetUtils.RelativeSequenceNumber(seq, _remoteSequence);

            if (relateSeq > _windowSize)
            {
                Peer.LogDebug("[RR]Bad sequence");
                return false;
            }

            //Drop bad packets
            if (relate < 0)
            {
                //Too old packet doesn't ack
                Peer.LogDebug("[RR]ReliableInOrder too old");
                return false;
            }

            if (relate >= _windowSize * 2)
            {
                //Some very new packet
                Peer.LogDebug("[RR]ReliableInOrder too new");
                return false;
            }

            //If very new - move window
            int ackIdx;
            int ackByte;
            int ackBit;
            await _outgoingAcksSem.WaitAsync();
            try
            {
                if (relate >= _windowSize)
                {
                    //New window position
                    var newWindowStart = (_remoteWindowStart + relate - _windowSize + 1) % NetConstants.MaxSequence;
                    var oa = _outgoingAcks;
                    oa.Sequence = (ushort)newWindowStart;
                    _outgoingAcks = oa;

                    //Clean old data
                    while (_remoteWindowStart != newWindowStart)
                    {
                        ackIdx = _remoteWindowStart % _windowSize;
                        ackByte = NetConstants.ChanneledHeaderSize + ackIdx / BitsInByte;
                        ackBit = ackIdx % BitsInByte;
                        _outgoingAcks.Data.Array[_outgoingAcks.Data.Offset + ackByte] &= (byte)~(1 << ackBit);
                        _remoteWindowStart = (_remoteWindowStart + 1) % NetConstants.MaxSequence;
                    }
                }

                //Final stage - process valid packet
                //trigger acks send
                _mustSendAcks = true;

                ackIdx = seq % _windowSize;
                ackByte = NetConstants.ChanneledHeaderSize + ackIdx / BitsInByte;
                ackBit = ackIdx % BitsInByte;
                if ((_outgoingAcks.Data[ackByte] & (1 << ackBit)) != 0)
                {
                    Peer.LogDebug("[RR]ReliableInOrder duplicate");
                    //because _mustSendAcks == true
                    AddToPeerChannelSendQueue();
                    return false;
                }

                //save ack
                _outgoingAcks.Data.Array[_outgoingAcks.Data.Offset + ackByte] |= (byte)(1 << ackBit);
            }
            finally
            {
                _outgoingAcksSem.Release();
            }

            AddToPeerChannelSendQueue();

            //detailed check
            if (seq == _remoteSequence)
            {
                //Peer.LogDebug($"[Receive] {packet.Property} ({packet.Data.Count} bytes) (sequence {packet.Sequence})");
                await Peer.AddReliablePacket(_deliveryMethod, packet);
                _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;

                if (_ordered)
                {
                    NetworkPacket? p;
                    while ((p = _receivedPackets[_remoteSequence % _windowSize]) != null)
                    {
                        //process holden packet
                        _receivedPackets[_remoteSequence % _windowSize] = null;
                        await Peer.AddReliablePacket(_deliveryMethod, p.Value);
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                else
                {
                    while (_earlyReceived[_remoteSequence % _windowSize])
                    {
                        //process early packet
                        _earlyReceived[_remoteSequence % _windowSize] = false;
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }

                return true;
            }

            //holden packet
            if (_ordered)
            {
                _receivedPackets[ackIdx] = packet;
            }
            else
            {
                _earlyReceived[ackIdx] = true;
                await Peer.AddReliablePacket(_deliveryMethod, packet);
            }

            return true;
        }

        private class PendingReliablePacket
        {
            private bool _isSent;
            private readonly NetworkPacket _packet;
            private long? _timeStamp;

            public override string ToString()
            {
                return _packet.Sequence.ToString();
            }

            public PendingReliablePacket(NetworkPacket packet)
            {
                this._packet = packet;
                _isSent = false;
                _timeStamp = null;
            }

            public async Task TrySendAsync(long utcNowTicks, PeerBase peer)
            {
                if (_isSent) //check send time
                {
                    var resendDelay = peer.ResendDelay * TimeSpan.TicksPerMillisecond;
                    double packetHoldTime = utcNowTicks - _timeStamp!.Value;
                    if (packetHoldTime < resendDelay) return;
                    peer.LogDebug($"[RC] Resend: {packetHoldTime} > {resendDelay}");
                }

                _timeStamp = utcNowTicks;
                _isSent = true;
                await peer.SendUserData(_packet);
            }

            public async Task ClearAsync(PeerBase peer)
            {
                await peer.RecycleAndDeliver(_packet);
            }
        }
    }
}