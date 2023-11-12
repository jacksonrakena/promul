using LiteNetLib.Utils;
namespace Promul.Server.Relay.Structs;

public static class NetDataExtensions
{
    public static RelayControlMessage Get(this NetDataReader reader)
    {
        var rcm = new RelayControlMessage();
        rcm.Type = (RelayControlMessageType) reader.GetByte();
        rcm.AuthorClientId = reader.GetULong();
        rcm.Data = reader.GetRemainingBytes();
        return rcm;
    }
    public static void Put(this NetDataWriter writer, RelayControlMessage rcm)
    {
        writer.Put((byte)rcm.Type);
        writer.Put(rcm.AuthorClientId);
        writer.Put(rcm.Data);
    }
}