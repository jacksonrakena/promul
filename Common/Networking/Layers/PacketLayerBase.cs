using System.Net;
using Promul.Common.Networking.Packets;

namespace Promul.Common.Networking.Layers
{
    public abstract class PacketLayerBase
    {
        public readonly int ExtraPacketSizeForLayer;

        protected PacketLayerBase(int extraPacketSizeForLayer)
        {
            ExtraPacketSizeForLayer = extraPacketSizeForLayer;
        }

        public abstract void ProcessInboundPacket(ref IPEndPoint endPoint,
            ref NetworkPacket data);

        public abstract void ProcessOutBoundPacket(ref IPEndPoint endPoint,
            ref NetworkPacket data);
    }
}