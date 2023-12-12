using System;
using System.Net;
namespace Promul
{
    internal sealed class NetConnectRequestPacket : ConnectPacketBase
    {
        public const int HeaderSize = 18;
        public readonly CompositeReader Data;
        public readonly byte[] TargetAddress;

        private NetConnectRequestPacket(long connectionTime, byte connectionNumber, int localId, byte[] targetAddress,
            CompositeReader data)
            : base(connectionTime, connectionNumber, localId)
        {
            TargetAddress = targetAddress;
            Data = data;
        }

        public static int GetProtocolId(NetworkPacket packet)
        {
            return BitConverter.ToInt32(packet.Data[1..]);
        }

        public static NetConnectRequestPacket? FromData(NetworkPacket packet)
        {
            if (packet.ConnectionNumber >= NetConstants.MaxConnectionNumber)
                return null;

            //Getting connection time for peer
            var connectionTime = BitConverter.ToInt64(packet.Data[5..]);

            //Get peer id
            var peerId = BitConverter.ToInt32(packet.Data[13..]);

            //Get target address
            int addrSize = packet.Data[HeaderSize - 1];
            if (addrSize != 16 && addrSize != 28)
                return null;
            var addressBytes = new byte[addrSize];
            Buffer.BlockCopy(packet.Data.Array, packet.Data.Offset + HeaderSize, addressBytes, 0, addrSize);

            // Read data and create request
            CompositeReader reader;
            if (packet.Data.Count > HeaderSize + addrSize)
                reader = CompositeReader.Create(new ArraySegment<byte>(packet.Data.Array,
                    packet.Data.Offset + HeaderSize + addrSize, packet.Data.Count - HeaderSize - addrSize));
            else reader = CompositeReader.Create(Array.Empty<byte>());

            return new NetConnectRequestPacket(connectionTime, packet.ConnectionNumber, peerId, addressBytes, reader);
        }

        public static NetworkPacket Make(ArraySegment<byte> connectData, SocketAddress addressBytes, long connectTime,
            int localId)
        {
            //Make initial packet
            var packet =
                NetworkPacket.FromProperty(PacketProperty.ConnectRequest, connectData.Count + addressBytes.Size);
            //Add data
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset + 1, NetConstants.ProtocolId);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset + 5, connectTime);
            FastBitConverter.GetBytes(packet.Data.Array, packet.Data.Offset + 13, localId);
            packet.Data.Array[packet.Data.Offset + HeaderSize - 1] = (byte)addressBytes.Size;
            for (var i = 0; i < addressBytes.Size; i++)
                packet.Data.Array[packet.Data.Offset + HeaderSize + i] = addressBytes[i];

            if (connectData.Array != null)
                connectData.CopyTo(packet.Data.Array, packet.Data.Offset + HeaderSize + addressBytes.Size);
            return packet;
        }
    }
}