using System;
using Promul.Common.Networking.Utils;

namespace Promul.Common.Networking
{
    public enum PacketProperty : byte
    {
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
        InvalidProtocol,
        NatMessage,
        Empty
    }

    public class NetPacket
    {
        private static readonly int PropertiesCount = Enum.GetValues(typeof(PacketProperty)).Length;
        private static readonly int[] HeaderSizes;

        public static implicit operator ArraySegment<byte>(NetPacket ndw)
        {
            return ndw.Data;
        }

        static NetPacket()
        {
            HeaderSizes = NetUtils.AllocatePinnedUninitializedArray<int>(PropertiesCount);
            for (int i = 0; i < HeaderSizes.Length; i++)
            {
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
            set => Data.Array[Data.Offset] = (byte) ((Data[0] & 0x9F) | (value << 5));
        }

        public ushort Sequence
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(1)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset+1, value);
        }

        public bool IsFragmented => (Data[0] & 0x80) != 0;

        public void MarkFragmented()
        {
            Data.Array[Data.Offset] |= 0x80; //set first bit
        }

        public byte ChannelId
        {
            get => Data[3];
            set => Data.Array[Data.Offset+3] = value;
        }

        public ushort FragmentId
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(4)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset+4, value);
        }

        public ushort FragmentPart
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(6)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset+6, value);
        }

        public ushort FragmentsTotal
        {
            get => BitConverter.ToUInt16(Data[Range.StartAt(8)]);
            set => FastBitConverter.GetBytes(Data.Array, Data.Offset+8, value);
        }

        //Data
        public ArraySegment<byte> Data { get; }
        public int Size => Data.Count;

        private NetPacket(ArraySegment<byte> data, PacketProperty property = default)
        {
            Data = data;
            Property = property;
        }

        public static NetPacket Empty(int size)
        {
            return new NetPacket(new byte[size]);
        }

        public static NetPacket FromBuffer(ArraySegment<byte> data)
        {
            return new NetPacket(data);
        }

        public static NetPacket FromProperty(PacketProperty property, int size)
        {
            return new NetPacket(new byte[size+GetHeaderSize(property)], property);
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
            byte property = (byte)(Data[0] & 0x1F);
            if (property >= PropertiesCount)
                return false;
            int headerSize = HeaderSizes[property];
            bool fragmented = (Data[0] & 0x80) != 0;
            return Data.Count >= headerSize && (!fragmented || Data.Count >= headerSize + NetConstants.FragmentHeaderSize);
        }
    }
}
