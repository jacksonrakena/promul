namespace Promul.Common.Networking.Data
{
    public interface INetSerializable
    {
        void Serialize(NetDataWriter writer);
        void Deserialize(NetDataReader reader);
    }
}
