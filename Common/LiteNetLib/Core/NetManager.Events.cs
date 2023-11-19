using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteNetLib.Utils;
namespace LiteNetLib
{
    public partial class NetManager
    {
        public delegate Task OnPeerConnectedEvent(NetPeer peer);
        public delegate Task OnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo);
        public delegate Task OnNetworkErrorEvent(IPEndPoint endPoint, SocketError socketError);
        public delegate Task OnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);
        public delegate Task OnConnectionlessReceiveEvent(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType);
        public delegate Task OnNetworkLatencyUpdateEvent(NetPeer peer, int latency);
        public delegate Task OnConnectionRequestEvent(ConnectionRequest request);
        public delegate Task OnDeliveryEventEvent(NetPeer peer, object userData);
        public delegate Task OnNtpResponseEventEvent(NtpPacket packet);
        public delegate Task OnPeerAddressChangedEventEvent(NetPeer peer, IPEndPoint previousAddress);
        
        /// <summary>
        ///     Invoked when a connection is made with a remote peer.
        /// </summary>
        public event OnPeerConnectedEvent OnPeerConnected;

        /// <summary>
        ///     Invoked when a connection is terminated with a remote peer.
        /// </summary>
        public event OnPeerDisconnectedEvent OnPeerDisconnected;

        /// <summary>
        ///     Invoked when a network error occurs on send or receive.
        /// </summary>
        public event OnNetworkErrorEvent OnNetworkError;

        /// <summary>
        ///     Invoked when information is received from a peer.
        /// </summary>
        public event OnNetworkReceiveEvent OnReceive;

        /// <summary>
        ///     Invoked when a message is received from a connectionless peer.
        ///     <see cref="UnconnectedMessagesEnabled"/> must be enabled for this event to be invoked.
        /// </summary>
        public event OnConnectionlessReceiveEvent OnConnectionlessReceive;

        /// <summary>
        ///     Invoked when latency is updated for a peer.
        /// </summary>
        public event OnNetworkLatencyUpdateEvent OnNetworkLatencyUpdate;

        /// <summary>
        ///     Invoked when a connection is received. The handler is expected to call <see cref="ConnectionRequest.AcceptAsync"/> or <see cref="ConnectionRequest.RejectAsync"/>.
        /// </summary>
        /// <param name="request">Request information (EndPoint, internal id, additional data)</param>
        public event OnConnectionRequestEvent OnConnectionRequest;
        
        public event OnDeliveryEventEvent OnMessageDelivered;
        
        public event OnNtpResponseEventEvent OnNtpResponse;
        
        /// <summary>
        /// Called when peer address changed (when AllowPeerAddressChange is enabled)
        /// </summary>
        /// <param name="peer">Peer that changed address (with new address)</param>
        /// <param name="previousAddress">previous IP</param>
        public event OnPeerAddressChangedEventEvent OnPeerAddressChanged;
    }
}