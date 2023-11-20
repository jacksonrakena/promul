using System;
using System.IO;
using Promul.Common.Networking.Data;

namespace Promul.Common.Structs
{
    public static class NetDataExtensions
    {
        public static RelayControlMessage ReadRelayControlMessage(this BinaryReader reader)
        {
            var rcm = new RelayControlMessage
            {
                Type = (RelayControlMessageType) reader.ReadByte(),
                AuthorClientId = reader.ReadUInt64(),
                Data = new ArraySegment<byte>(reader.ReadBytes(int.MaxValue))
            };
            /*
             *             rcm.Type = (RelayControlMessageType) reader.Data.Array[reader.Data.Offset];
            rcm.AuthorClientId = BitConverter.ToUInt64(reader.Data.Array, reader.Data.Offset+1);
            rcm.Data = new ArraySegment<byte>(reader.Data.Array, reader.Data.Offset + 9, reader.Data.Count - 9);
             */
            return rcm;
        }
        public static void Write(this BinaryWriter writer, RelayControlMessage rcm)
        {
            writer.Write((byte)rcm.Type);
            writer.Write(rcm.AuthorClientId);
            writer.Write(rcm.Data);
        }
    }
}