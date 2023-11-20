using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Promul.Common.Networking
{
    internal abstract class ChannelBase
    {
        protected readonly NetPeer Peer;
        protected readonly Queue<NetPacket> OutgoingQueue = new(NetConstants.DefaultWindowSize);
        private int _isAddedToPeerChannelSendQueue;

        public int PacketsInQueue => OutgoingQueue.Count;

        protected ChannelBase(NetPeer peer)
        {
            Peer = peer;
        }

        public void EnqueuePacket(NetPacket packet)
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

        /// <summary>
        /// Flushes (sends) all queued packets, if any exist.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> UpdateQueueAsync()
        {
            bool hasPacketsToSend = await FlushQueueAsync();
            if (!hasPacketsToSend)
                Interlocked.Exchange(ref _isAddedToPeerChannelSendQueue, 0);

            return hasPacketsToSend;
        }

        /// <summary>
        /// Called periodically by the system to flush (send) all queued packets.
        /// </summary>
        protected abstract Task<bool> FlushQueueAsync();
        
        /// <summary>
        /// Called when this channel receives a new packet from the remote peer.
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        public abstract Task<bool> HandlePacketAsync(NetPacket packet);
    }
}
