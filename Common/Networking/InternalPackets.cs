using System;
using System.Net;
using Promul.Common.Networking.Data;
using Promul.Common.Networking.Utils;

namespace Promul.Common.Networking
{
   internal sealed class NetConnectRequestPacket
    {
        public const int HeaderSize = 18;
        public readonly long ConnectionTime;
        public byte ConnectionNumber;
        public readonly byte[] TargetAddress;
        public readonly NetDataReader Data;
        public readonly int PeerId;

        private NetConnectRequestPacket(long connectionTime, byte connectionNumber, int localId, byte[] targetAddress, NetDataReader data)
        {
            ConnectionTime = connectionTime;
            ConnectionNumber = connectionNumber;
            TargetAddress = targetAddress;
            Data = data;
            PeerId = localId;
        }

        public static int GetProtocolId(NetPacket packet)
        {
            return BitConverter.ToInt32(packet.Data[1..]);
        }

        public static NetConnectRequestPacket FromData(NetPacket packet)
        {
            if (packet.ConnectionNumber >= NetConstants.MaxConnectionNumber)
                return null;

            //Getting connection time for peer
            long connectionTime = BitConverter.ToInt64(packet.Data[5..]);

            //Get peer id
            int peerId = BitConverter.ToInt32(packet.Data[13..]);

            //Get target address
            int addrSize = packet.Data[HeaderSize - 1];
            if (addrSize != 16 && addrSize != 28)
                return null;
            byte[] addressBytes = new byte[addrSize];
            Buffer.BlockCopy(packet.Data.Array, packet.Data.Offset+HeaderSize, addressBytes, 0, addrSize);

            // Read data and create request
            NetDataReader? reader = null;
            if (packet.Data.Count > HeaderSize+addrSize)
                reader = new NetDataReader(new ArraySegment<byte>(packet.Data.Array, packet.Data.Offset + HeaderSize + addrSize, packet.Data.Count));

            return new NetConnectRequestPacket(connectionTime, packet.ConnectionNumber, peerId, addressBytes, reader);
        }

        public static NetPacket? Make(ArraySegment<byte> connectData, SocketAddress addressBytes, long connectTime, int localId)
        {
            //Make initial packet
            var packet = NetPacket.FromProperty(PacketProperty.ConnectRequest, connectData.Count + addressBytes.Size);
   
            //Add data
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+1, NetConstants.ProtocolId);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+5, connectTime);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+13, localId);
            packet.Data.Array[packet.Data.Offset + HeaderSize - 1] = (byte)addressBytes.Size;
            for (int i = 0; i < addressBytes.Size; i++)
                packet.Data.Array[packet.Data.Offset + HeaderSize + i] = addressBytes[i];

            if (connectData.Array != null)
            {
                connectData.CopyTo(packet.Data.Array, packet.Data.Offset + HeaderSize + addressBytes.Size);
            }
            return packet;
        }
    }

    internal sealed class NetConnectAcceptPacket
    {
        public const int Size = 15;
        public readonly long ConnectionTime;
        public readonly byte ConnectionNumber;
        public readonly int PeerId;
        public readonly bool PeerNetworkChanged;

        private NetConnectAcceptPacket(long connectionTime, byte connectionNumber, int peerId, bool peerNetworkChanged)
        {
            ConnectionTime = connectionTime;
            ConnectionNumber = connectionNumber;
            PeerId = peerId;
            PeerNetworkChanged = peerNetworkChanged;
        }

        public static NetConnectAcceptPacket FromData(NetPacket packet)
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

        public static NetPacket? Make(long connectTime, byte connectNum, int localPeerId)
        {
            var packet = NetPacket.FromProperty(PacketProperty.ConnectAccept, 0);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+1, connectTime);
            packet.Data.Array[packet.Data.Offset+9] = connectNum;
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+11, localPeerId);
            return packet;
        }
        
        public static NetPacket MakeNetworkChanged(NetPeer peer)
        {
            var packet = NetPacket.FromProperty(PacketProperty.PeerNotFound, Size - 1);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+1, peer.ConnectTime);
            packet.Data.Array[packet.Data.Offset+9] = peer.ConnectionNum;
            packet.Data.Array[packet.Data.Offset+10] = 1;
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset+11, peer.RemoteId);
            return packet;
        }
    }
}