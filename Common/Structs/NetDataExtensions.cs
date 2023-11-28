using System;
using System.IO;

namespace Promul.Common.Structs
{
    public static class NetDataExtensions
    {
        public static RelayControlMessage ReadRelayControlMessage(this BinaryReader reader)
        {
            var ms = (MemoryStream)reader.BaseStream;
            var rcm = new RelayControlMessage
            {
                Type = (RelayControlMessageType) reader.ReadByte(),
                AuthorClientId = reader.ReadUInt64(),
                Data = new ArraySegment<byte>(ms.GetBuffer(), (int)ms.Position, (int)(ms.Capacity-ms.Position))
            };
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