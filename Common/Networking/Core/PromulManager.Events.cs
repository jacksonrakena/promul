using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Promul.Common.Networking.Data;

namespace Promul.Common.Networking
{
    public partial class PromulManager
    {
        public delegate Task PeerConnectedEvent(PeerBase peer);
        public delegate Task PeerDisconnectedEvent(PeerBase peer, DisconnectInfo disconnectInfo);
        public delegate Task NetworkErrorEvent(IPEndPoint endPoint, SocketError socketError);
        public delegate Task NetworkReceiveEvent(PeerBase peer, CompositeReader reader, byte channel, DeliveryMethod deliveryMethod);
        public delegate Task ConnectionlessReceiveEvent(IPEndPoint remoteEndPoint, CompositeReader reader, UnconnectedMessageType messageType);
        public delegate Task NetworkLatencyUpdateEvent(PeerBase peer, int latency);
        public delegate Task ConnectionRequestEvent(ConnectionRequest request);
        public delegate Task DeliveryEvent(PeerBase peer, object? userData);
        public delegate Task NtpResponseEvent(NtpPacket packet);
        public delegate Task PeerAddressChangedEvent(PeerBase peer, IPEndPoint previousAddress);
        
        /// <summary>
        ///     Invoked when a connection is made with a remote peer.
        /// </summary>
        public event PeerConnectedEvent? OnPeerConnected;

        /// <summary>
        ///     Invoked when a connection is terminated with a remote peer.
        /// </summary>
        public event PeerDisconnectedEvent? OnPeerDisconnected;

        /// <summary>
        ///     Invoked when a network error occurs on send or receive.
        /// </summary>
        public event NetworkErrorEvent? OnNetworkError;

        /// <summary>
        ///     Invoked when information is received from a peer.
        /// </summary>
        public event NetworkReceiveEvent? OnReceive;

        /// <summary>
        ///     Invoked when a message is received from a connectionless peer.
        ///     <see cref="ConnectionlessMessagesAllowed"/> must be enabled for this event to be invoked.
        /// </summary>
        public event ConnectionlessReceiveEvent? OnConnectionlessReceive;

        /// <summary>
        ///     Invoked when latency is updated for a peer.
        /// </summary>
        public event NetworkLatencyUpdateEvent? OnNetworkLatencyUpdate;

        /// <summary>
        ///     Invoked when a connection is received. The handler is expected to call <see cref="ConnectionRequest.AcceptAsync"/> or <see cref="ConnectionRequest.RejectAsync"/>.
        /// </summary>
        public event ConnectionRequestEvent? OnConnectionRequest;
        
        public event DeliveryEvent? OnMessageDelivered;
        
        public event NtpResponseEvent? OnNtpResponse;
        
        /// <summary>
        /// Called when a peer address changes. This event is only called when <see cref="AllowPeerAddressChange"/> is enabled.
        /// </summary>
        public event PeerAddressChangedEvent? OnPeerAddressChanged;
    }
}