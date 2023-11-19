using System;
namespace Promul.Common.Structs
{
    public struct RelayControlMessage {
        public RelayControlMessageType Type { get; set; }
        public ulong AuthorClientId { get; set; }
        public ArraySegment<byte> Data { get; set; }
    }
}