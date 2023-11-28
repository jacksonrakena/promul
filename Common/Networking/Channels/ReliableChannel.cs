using System;
using System.Threading;
using System.Threading.Tasks;
using Promul.Common.Networking.Packets;

namespace Promul.Common.Networking.Channels
{
    internal sealed class ReliableChannel : ChannelBase
    {
        private class PendingReliablePacket
        {
            private NetworkPacket? _packet;
            private long _timeStamp;
            private bool _isSent;

            public override string ToString()
            {
                return _packet == null ? "Empty" : _packet.Sequence.ToString();
            }

            public void Init(NetworkPacket packet)
            {
                _packet = packet;
                _isSent = false;
            }

            //Returns true if there is a pending packet inside
            public async Task<bool> TrySendAsync(long currentTime, PeerBase peer)
            {
                if (_packet == null)
                    return false;

                if (_isSent) //check send time
                {
                    double resendDelay = peer.ResendDelay * TimeSpan.TicksPerMillisecond;
                    double packetHoldTime = currentTime - _timeStamp;
                    if (packetHoldTime < resendDelay) return true;
                    NetDebug.Write($"[RC]Resend: {packetHoldTime} > {resendDelay}");
                }
                _timeStamp = currentTime;
                _isSent = true;
                await peer.SendUserData(_packet);
                return true;
            }

            public async Task<bool> ClearAsync(PeerBase peer)
            {
                if (_packet != null)
                {
                    await peer.RecycleAndDeliver(_packet);
                    _packet = null;
                    return true;
                }
                return false;
            }
        }

        private readonly NetworkPacket _outgoingAcks;            //for send acks
        private readonly PendingReliablePacket[] _pendingPackets;    //for unacked packets and duplicates
        private readonly NetworkPacket[]? _receivedPackets;       //for order
        private readonly bool[]? _earlyReceived;              //for unordered

        private int _localSequence;
        private int _remoteSequence;
        private int _localWindowStart;
        private int _remoteWindowStart;

        private bool _mustSendAcks;

        private readonly DeliveryMethod _deliveryMethod;
        private readonly bool _ordered;
        private readonly int _windowSize;
        private const int BitsInByte = 8;
        private readonly byte _id;

        public ReliableChannel(PeerBase peer, bool ordered, byte id) : base(peer)
        {
            _id = id;
            _windowSize = NetConstants.DefaultWindowSize;
            _ordered = ordered;
            _pendingPackets = new PendingReliablePacket[_windowSize];
            for (int i = 0; i < _pendingPackets.Length; i++)
                _pendingPackets[i] = new PendingReliablePacket();

            if (_ordered)
            {
                _deliveryMethod = DeliveryMethod.ReliableOrdered;
                _receivedPackets = new NetworkPacket[_windowSize];
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

        private SemaphoreSlim _pendingPacketsSemaphore = new SemaphoreSlim(1, 1);
        //ProcessAck in packet
        private async Task ProcessAckAsync(NetworkPacket packet)
        {
            if (packet.Data.Count != _outgoingAcks.Data.Count)
            {
                NetDebug.Write("[PA]Invalid acks packet size");
                return;
            }

            ushort ackWindowStart = packet.Sequence;
            int windowRel = NetUtils.RelativeSequenceNumber(_localWindowStart, ackWindowStart);
            if (ackWindowStart >= NetConstants.MaxSequence || windowRel < 0)
            {
                NetDebug.Write("[PA]Bad window start");
                return;
            }

            //check relevance
            if (windowRel >= _windowSize)
            {
                NetDebug.Write("[PA]Old acks");
                return;
            }

            await _pendingPacketsSemaphore.WaitAsync();
            try
            {
                for (int pendingSeq = _localWindowStart;
                     pendingSeq != _localSequence;
                     pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    int rel = NetUtils.RelativeSequenceNumber(pendingSeq, ackWindowStart);
                    if (rel >= _windowSize)
                    {
                        NetDebug.Write("[PA]REL: " + rel);
                        break;
                    }

                    int pendingIdx = pendingSeq % _windowSize;
                    int currentByte = NetConstants.ChanneledHeaderSize + pendingIdx / BitsInByte;
                    int currentBit = pendingIdx % BitsInByte;
                    if ((packet.Data[packet.Data.Offset + currentByte] & (1 << currentBit)) == 0)
                    {
                        if (Peer.PromulManager.RecordNetworkStatistics)
                        {
                            Peer.Statistics.IncrementPacketLoss();
                            Peer.PromulManager.Statistics.IncrementPacketLoss();
                        }

                        //Skip false ack
                        NetDebug.Write($"[PA]False ack: {pendingSeq}");
                        continue;
                    }

                    if (pendingSeq == _localWindowStart)
                    {
                        //Move window
                        _localWindowStart = (_localWindowStart + 1) % NetConstants.MaxSequence;
                    }

                    //clear packet
                    if (await _pendingPackets[pendingIdx].ClearAsync(Peer))
                        NetDebug.Write($"[PA]Removing reliableInOrder ack: {pendingSeq} - true");
                }
            }
            finally
            {
                _pendingPacketsSemaphore.Release();
            }
        }

        SemaphoreSlim pendingPacketsSem = new SemaphoreSlim(1, 1);
        SemaphoreSlim outgoingAcksSem = new SemaphoreSlim(1, 1);
        protected override async Task<bool> FlushQueueAsync()
        {
            if (_mustSendAcks)
            {
                _mustSendAcks = false;
                await outgoingAcksSem.WaitAsync();
                try { await Peer.SendUserData(_outgoingAcks); }
                finally { outgoingAcksSem.Release(); }
            }

            long currentTime = DateTime.UtcNow.Ticks;
            bool hasPendingPackets = false;

            await pendingPacketsSem.WaitAsync();
            try
            {
                await outgoingQueueSem.WaitAsync();
                try
                {
                    while (OutgoingQueue.Count > 0)
                    {
                        int relate = NetUtils.RelativeSequenceNumber(_localSequence, _localWindowStart);
                        if (relate >= _windowSize)
                            break;

                        var netPacket = OutgoingQueue.Dequeue();
                        netPacket.Sequence = (ushort)_localSequence;
                        netPacket.ChannelId = _id;
                        _pendingPackets[_localSequence % _windowSize].Init(netPacket);
                        _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                finally
                {
                    outgoingQueueSem.Release();
                }

                //send
                for (int pendingSeq = _localWindowStart;
                     pendingSeq != _localSequence;
                     pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    // Please note: TrySend is invoked on a mutable struct, it's important to not extract it into a variable here
                    if (await _pendingPackets[pendingSeq % _windowSize].TrySendAsync(currentTime, Peer))
                    {
                        hasPendingPackets = true;
                    }
                }
            }
            finally
            {
                pendingPacketsSem.Release();
            }
            
            return hasPendingPackets || _mustSendAcks || OutgoingQueue.Count > 0;
        }


        //Process incoming packet
        public override async Task<bool> HandlePacketAsync(NetworkPacket packet)
        {
            if (packet.Property == PacketProperty.Ack)
            {
                await ProcessAckAsync(packet);
                return false;
            }
            int seq = packet.Sequence;
            if (seq >= NetConstants.MaxSequence)
            {
                NetDebug.Write("[RR]Bad sequence");
                return false;
            }

            int relate = NetUtils.RelativeSequenceNumber(seq, _remoteWindowStart);
            int relateSeq = NetUtils.RelativeSequenceNumber(seq, _remoteSequence);

            if (relateSeq > _windowSize)
            {
                NetDebug.Write("[RR]Bad sequence");
                return false;
            }

            //Drop bad packets
            if (relate < 0)
            {
                //Too old packet doesn't ack
                NetDebug.Write("[RR]ReliableInOrder too old");
                return false;
            }
            if (relate >= _windowSize * 2)
            {
                //Some very new packet
                NetDebug.Write("[RR]ReliableInOrder too new");
                return false;
            }

            //If very new - move window
            int ackIdx;
            int ackByte;
            int ackBit;
            await outgoingAcksSem.WaitAsync();
            try
            {
                if (relate >= _windowSize)
                {
                    //New window position
                    int newWindowStart = (_remoteWindowStart + relate - _windowSize + 1) % NetConstants.MaxSequence;
                    _outgoingAcks.Sequence = (ushort)newWindowStart;

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
                if ((_outgoingAcks.Data.Array[_outgoingAcks.Data.Offset + ackByte] & (1 << ackBit)) != 0)
                {
                    NetDebug.Write("[RR]ReliableInOrder duplicate");
                    //because _mustSendAcks == true
                    AddToPeerChannelSendQueue();
                    return false;
                }

                //save ack
                _outgoingAcks.Data.Array[_outgoingAcks.Data.Offset + ackByte] |= (byte)(1 << ackBit);
            }
            finally
            {
                outgoingAcksSem.Release();   
            }

            AddToPeerChannelSendQueue();

            //detailed check
            if (seq == _remoteSequence)
            {
                NetDebug.Write("[RR]ReliableInOrder packet succes");
                await Peer.AddReliablePacket(_deliveryMethod, packet);
                _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;

                if (_ordered)
                {
                    NetworkPacket p;
                    while ((p = _receivedPackets[_remoteSequence % _windowSize]) != null)
                    {
                        //process holden packet
                        _receivedPackets[_remoteSequence % _windowSize] = null;
                        await Peer.AddReliablePacket(_deliveryMethod, p);
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
    }
}
