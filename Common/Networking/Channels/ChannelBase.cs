using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Promul.Common.Networking.Packets;

namespace Promul.Common.Networking.Channels
{
    internal abstract class ChannelBase
    {
        protected readonly PeerBase Peer;
        protected readonly Queue<NetworkPacket> OutgoingQueue = new(NetConstants.DefaultWindowSize);
        protected SemaphoreSlim outgoingQueueSem = new SemaphoreSlim(1, 1);
        private int _isAddedToPeerChannelSendQueue;

        public int PacketsInQueue => OutgoingQueue.Count;

        protected ChannelBase(PeerBase peer)
        {
            Peer = peer;
        }

        public async Task EnqueuePacketAsync(NetworkPacket packet)
        {
            await outgoingQueueSem.WaitAsync();
            
            OutgoingQueue.Enqueue(packet);
            
            outgoingQueueSem.Release();
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
        public abstract Task<bool> HandlePacketAsync(NetworkPacket packet);
    }
}
