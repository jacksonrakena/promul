namespace Promul.Common.Structs
{
    public struct RelayControlMessage {
        public RelayControlMessageType Type { get; set; }
        public ulong AuthorClientId { get; set; }
        public byte[] Data { get; set; }
    }
}