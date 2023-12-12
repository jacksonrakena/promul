using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
namespace Promul
{
    public partial class PromulManager
    {
        public delegate ValueTask ConnectionlessReceiveEvent(IPEndPoint remoteEndPoint, CompositeReader reader,
            UnconnectedMessageType messageType);

        public delegate ValueTask ConnectionRequestEvent(ConnectionRequest request);

        public delegate ValueTask DeliveryEvent(PeerBase peer, object? userData);

        public delegate ValueTask NetworkErrorEvent(IPEndPoint endPoint, SocketError socketError);

        public delegate ValueTask NetworkLatencyUpdateEvent(PeerBase peer, int latency);

        public delegate ValueTask NetworkReceiveEvent(PeerBase peer, CompositeReader reader, byte channel,
            DeliveryMethod deliveryMethod);

        public delegate ValueTask NtpResponseEvent(NtpPacket packet);

        public delegate ValueTask PeerAddressChangedEvent(PeerBase peer, IPEndPoint previousAddress);

        public delegate ValueTask PeerConnectedEvent(PeerBase peer);

        public delegate ValueTask PeerDisconnectedEvent(PeerBase peer, DisconnectInfo disconnectInfo);

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
        ///     <see cref="ConnectionlessMessagesAllowed" /> must be enabled for this event to be invoked.
        /// </summary>
        public event ConnectionlessReceiveEvent? OnConnectionlessReceive;

        /// <summary>
        ///     Invoked when latency is updated for a peer.
        /// </summary>
        public event NetworkLatencyUpdateEvent? OnNetworkLatencyUpdate;

        /// <summary>
        ///     Invoked when a connection is received. The handler is expected to call <see cref="ConnectionRequest.AcceptAsync" />
        ///     or <see cref="ConnectionRequest.RejectAsync" />.
        /// </summary>
        public event ConnectionRequestEvent? OnConnectionRequest;

        public event DeliveryEvent? OnMessageDelivered;

        public event NtpResponseEvent? OnNtpResponse;

        /// <summary>
        ///     Called when a peer address changes. This event is only called when <see cref="AllowPeerAddressChange" /> is
        ///     enabled.
        /// </summary>
        public event PeerAddressChangedEvent? OnPeerAddressChanged;
    }
}