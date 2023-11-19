using System;
using System.Net;

namespace LiteNetLib.Layers
{
    public abstract class PacketLayerBase
    {
        public readonly int ExtraPacketSizeForLayer;

        protected PacketLayerBase(int extraPacketSizeForLayer)
        {
            ExtraPacketSizeForLayer = extraPacketSizeForLayer;
        }

        public abstract void ProcessInboundPacket(ref IPEndPoint endPoint, 
            ref NetPacket data);
        public abstract void ProcessOutBoundPacket(ref IPEndPoint endPoint, 
            ref NetPacket data);
    }
}
