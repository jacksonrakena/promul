using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Promul.Common.Networking.Data;
using Promul.Common.Networking.Utils;

namespace Promul.Common.Networking
{
    public partial class NetManager
    {
        public delegate Task PeerConnectedEvent(NetPeer peer);
        public delegate Task PeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo);
        public delegate Task NetworkErrorEvent(IPEndPoint endPoint, SocketError socketError);
        public delegate Task NetworkReceiveEvent(NetPeer peer, BinaryReader reader, byte channel, DeliveryMethod deliveryMethod);
        public delegate Task ConnectionlessReceiveEvent(IPEndPoint remoteEndPoint, BinaryReader reader, UnconnectedMessageType messageType);
        public delegate Task NetworkLatencyUpdateEvent(NetPeer peer, int latency);
        public delegate Task ConnectionRequestEvent(ConnectionRequest request);
        public delegate Task DeliveryEvent(NetPeer peer, object userData);
        public delegate Task NtpResponseEvent(NtpPacket packet);
        public delegate Task PeerAddressChangedEvent(NetPeer peer, IPEndPoint previousAddress);
        
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