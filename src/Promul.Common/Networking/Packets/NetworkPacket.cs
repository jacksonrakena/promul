using System;
using Promul.Common.Networking.Data;
using Promul.Common.Networking.Packets.Internal;

namespace Promul.Common.Networking.Packets
{
    public enum PacketProperty : byte
    {
        Unknown,
        Unreliable,
        Channeled,
        Ack,
        Ping,
        Pong,
        ConnectRequest,
        ConnectAccept,
        Disconnect,
        UnconnectedMessage,
        MtuCheck,
        MtuOk,
        Broadcast,
        Merged,
        ShutdownOk,
        PeerNotFound,
        InvalidProtocol
    }

    public readonly struct NetworkPacket
    {
        private static readonly int PropertiesCount = Enum.GetValues(typeof(PacketProperty)).Length;
        private static readonly int[] HeaderSizes;

        static NetworkPacket()
        {
            HeaderSizes = NetUtils.AllocatePinnedUninitializedArray<int>(PropertiesCount);
            for (var i = 0; i < HeaderSizes.Length; i++)
                switch ((PacketProperty)i)
                {
                    case PacketProperty.Channeled:
                    case PacketProperty.Ack:
                        HeaderSizes[i] = NetConstants.ChanneledHeaderSize;
                        break;
                    case PacketProperty.Ping:
                        HeaderSizes[i] = NetConstants.HeaderSize + 2;
                        break;
                    case PacketProperty.ConnectRequest:
                        HeaderSizes[i] = NetConnectRequestPacket.HeaderSize;
                        break;
                    case PacketProperty.ConnectAccept:
                        HeaderSizes[i] = NetConnectAcceptPacket.Size;
                        break;
                    case PacketProperty.Disconnect:
                        HeaderSizes[i] = NetConstants.HeaderSize + 8;
                        break;
                    case PacketProperty.Pong:
                        HeaderSizes[i] = NetConstants.HeaderSize + 10;
                        break;
                    default:
                        HeaderSizes[i] = NetConstants.HeaderSize;
                        break;
                }
        }

        private NetworkPacket(ArraySegment<byte> data)
        {
            Data = data;
        }

        private NetworkPacket(ArraySegment<byte> data, PacketProperty property)
        {
            Data = data;
            Property = property;
        }

        //Header
        public PacketProperty Property
        {
            get => (PacketProperty)(Data[0] & 0x1F);
            set => Data.Array[Data.Offset] = (byte)((Data[0] & 0xE0) | (byte)value);
        }

        public byte ConnectionNumber
        {
            get => (byte)((Data[0] & 0x60) >> 5);
            set => Data.Array[Data.Offset] = (byte)((Data[0] & 0x9F) | (value << 5));
        }

        public ushort Sequence
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(1)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset + 1, value);
        }

        public bool IsFragmented => (Data[0] & 0x80) != 0;

        public byte ChannelId
        {
            get => Data[3];
            set => Data.Array[Data.Offset + 3] = value;
        }

        public ushort FragmentId
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(4)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset + 4, value);
        }

        public ushort FragmentPart
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(6)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset + 6, value);
        }

        public ushort FragmentsTotal
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(8)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset + 8, value);
        }

        //Data
        public ArraySegment<byte> Data { get; }

        public static implicit operator ArraySegment<byte>(NetworkPacket ndw)
        {
            return ndw.Data;
        }

        public void MarkFragmented()
        {
            Data.Array[Data.Offset] |= 0x80; //set first bit
        }

        public static NetworkPacket FromBuffer(ArraySegment<byte> data)
        {
            return new NetworkPacket(data);
        }

        public static NetworkPacket FromProperty(PacketProperty property, int size)
        {
            return new NetworkPacket(new byte[size + GetHeaderSize(property)], property);
        }

        public static int GetHeaderSize(PacketProperty property)
        {
            return HeaderSizes[(int)property];
        }

        public int GetHeaderSize()
        {
            return HeaderSizes[Data[0] & 0x1F];
        }

        public bool Verify()
        {
            var property = (byte)(Data[0] & 0x1F);
            if (property >= PropertiesCount)
                return false;
            var headerSize = HeaderSizes[property];
            var fragmented = (Data[0] & 0x80) != 0;
            return Data.Count >= headerSize &&
                   (!fragmented || Data.Count >= headerSize + NetConstants.FragmentHeaderSize);
        }

        public CompositeReader CreateReader(int headerSize)
        {
            var compositeData = new ArraySegment<byte>(Data.Array, Data.Offset + headerSize, Data.Count - headerSize);
            return CompositeReader.Create(compositeData);
        }
    }
}