using System;

namespace Promul.Common.Networking.Packets.Internal
{
    internal sealed class NetConnectAcceptPacket : ConnectPacketBase
    {
        public const int Size = 15;
        public readonly int PeerId;
        public readonly bool PeerNetworkChanged;

        private NetConnectAcceptPacket(long connectionTime, byte connectionNumber, int peerId, bool peerNetworkChanged)
            : base(connectionTime, connectionNumber, peerId)
        {
            PeerNetworkChanged = peerNetworkChanged;
        }

        public static NetConnectAcceptPacket? FromData(NetworkPacket packet)
        {
            if (packet.Data.Count != Size)
                return null;

            long connectionId = BitConverter.ToInt64(packet.Data[1..]);

            //check connect num
            byte connectionNumber = packet.Data[9];
            if (connectionNumber >= NetConstants.MaxConnectionNumber)
                return null;

            //check reused flag
            byte isReused = packet.Data[10];
            if (isReused > 1)
                return null;

            //get remote peer id
            int peerId = BitConverter.ToInt32(packet.Data[11..]);
            if (peerId < 0)
                return null;

            return new NetConnectAcceptPacket(connectionId, connectionNumber, peerId, isReused == 1);
        }

        public static NetworkPacket Make(long connectTime, byte connectNum, int localPeerId)
        {
            var packet = NetworkPacket.FromProperty(PacketProperty.ConnectAccept, 0);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+1, connectTime);
            packet.Data.Array[packet.Data.Offset+9] = connectNum;
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+11, localPeerId);
            return packet;
        }
        
        public static NetworkPacket MakeNetworkChanged(PeerBase peer)
        {
            var packet = NetworkPacket.FromProperty(PacketProperty.PeerNotFound, Size - 1);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+1, peer.ConnectTime);
            packet.Data.Array[packet.Data.Offset+9] = peer.ConnectionNumber;
            packet.Data.Array[packet.Data.Offset+10] = 1;
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+11, peer.RemoteId);
            return packet;
        }
    }
}