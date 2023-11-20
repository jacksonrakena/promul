using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Promul.Common.Networking
{
    internal abstract class BaseChannel
    {
        protected readonly NetPeer Peer;
        protected readonly Queue<NetPacket> OutgoingQueue = new Queue<NetPacket>(NetConstants.DefaultWindowSize);
        private int _isAddedToPeerChannelSendQueue;

        public int PacketsInQueue => OutgoingQueue.Count;

        protected BaseChannel(NetPeer peer)
        {
            Peer = peer;
        }

        public void AddToQueue(NetPacket packet)
        {
            lock (OutgoingQueue)
            {
                OutgoingQueue.Enqueue(packet);
            }
            AddToPeerChannelSendQueue();
        }

        protected void AddToPeerChannelSendQueue()
        {
            if (Interlocked.CompareExchange(ref _isAddedToPeerChannelSendQueue, 1, 0) == 0)
            {
                Peer.AddToReliableChannelSendQueue(this);
            }
        }

        public async Task<bool> SendAndCheckQueue()
        {
            bool hasPacketsToSend = await SendNextPackets();
            if (!hasPacketsToSend)
                Interlocked.Exchange(ref _isAddedToPeerChannelSendQueue, 0);

            return hasPacketsToSend;
        }

        protected abstract Task<bool> SendNextPackets();
        public abstract Task<bool> ProcessPacket(NetPacket packet);
    }
}
