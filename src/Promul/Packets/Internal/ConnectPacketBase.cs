namespace Promul
{
    public abstract class ConnectPacketBase
    {
        public readonly long ConnectionTime;
        public readonly int PeerId;
        public byte ConnectionNumber;

        public ConnectPacketBase(long connectionTime, byte connectionNumber, int peerId)
        {
            ConnectionNumber = connectionNumber;
            ConnectionTime = connectionTime;
            PeerId = peerId;
        }
    }
}