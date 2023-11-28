using System;
using System.IO;
using System.Linq;
using System.Net;

namespace Promul.Common.Networking.Packets.Internal
{
    internal sealed class NetConnectRequestPacket
    {
        public const int HeaderSize = 18;
        public readonly long ConnectionTime;
        public byte ConnectionNumber;
        public readonly byte[] TargetAddress;
        public readonly BinaryReader Data;
        public readonly int PeerId;

        private NetConnectRequestPacket(long connectionTime, byte connectionNumber, int localId, byte[] targetAddress, BinaryReader data)
        {
            ConnectionTime = connectionTime;
            ConnectionNumber = connectionNumber;
            TargetAddress = targetAddress;
            Data = data;
            PeerId = localId;
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
            BinaryReader? reader = null;
            if (packet.Data.Count > HeaderSize + addrSize)
                reader = new BinaryReader(new MemoryStream(packet.Data.Array,
                    packet.Data.Offset + HeaderSize + addrSize, packet.Data.Count));
            else reader = new BinaryReader(new MemoryStream());

            return new NetConnectRequestPacket(connectionTime, packet.ConnectionNumber, peerId, addressBytes, reader);
        }
        public static NetworkPacket Make(ArraySegment<byte> connectData, SocketAddress addressBytes, long connectTime, int localId)
        {
            //Make initial packet
            var packet = NetworkPacket.FromProperty(PacketProperty.ConnectRequest, connectData.Count + addressBytes.Size);
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
}