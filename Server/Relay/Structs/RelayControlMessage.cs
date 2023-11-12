using Promul.Relay.Structs;
namespace Promul;

public struct RelayControlMessage {
    public RelayControlMessageType Type { get; set; }
    public ulong AuthorClientId { get; set; }
    public byte[] Data { get; set; }
}