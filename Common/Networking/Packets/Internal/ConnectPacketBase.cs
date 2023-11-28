namespace Promul.Common.Networking.Packets.Internal
{
    public abstract class ConnectPacketBase
    {
        public readonly long ConnectionTime;
        public byte ConnectionNumber;
        public readonly int PeerId;

        public ConnectPacketBase(long connectionTime, byte connectionNumber, int peerId)
        {
            ConnectionNumber = connectionNumber;
            ConnectionTime = connectionTime;
            PeerId = peerId;
        }
    }
}