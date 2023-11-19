using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    /// <summary>
    /// Type of message that you receive in OnNetworkReceiveUnconnected event
    /// </summary>
    public enum UnconnectedMessageType
    {
        BasicMessage,
        Broadcast
    }

    /// <summary>
    /// Disconnect reason that you receive in OnPeerDisconnected event
    /// </summary>
    public enum DisconnectReason
    {
        ConnectionFailed,
        Timeout,
        HostUnreachable,
        NetworkUnreachable,
        RemoteConnectionClose,
        DisconnectPeerCalled,
        ConnectionRejected,
        InvalidProtocol,
        UnknownHost,
        Reconnect,
        PeerToPeerConnection,
        PeerNotFound
    }

    /// <summary>
    /// Additional information about disconnection
    /// </summary>
    public struct DisconnectInfo
    {
        /// <summary>
        /// Additional info why peer disconnected
        /// </summary>
        public DisconnectReason Reason;

        /// <summary>
        /// Error code (if reason is SocketSendError or SocketReceiveError)
        /// </summary>
        public SocketError SocketErrorCode;

        /// <summary>
        /// Additional data that can be accessed (only if reason is RemoteConnectionClose)
        /// </summary>
        public NetPacketReader AdditionalData;
    }

    public interface INetEventListener
    {
        /// <summary>
        /// New remote peer connected to host, or client connected to remote host
        /// </summary>
        /// <param name="peer">Connected peer object</param>
        Task OnPeerConnected(NetPeer peer);

        /// <summary>
        /// Peer disconnected
        /// </summary>
        /// <param name="peer">disconnected peer</param>
        /// <param name="disconnectInfo">additional info about reason, errorCode or data received with disconnect message</param>
        Task OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo);

        /// <summary>
        /// Network error (on send or receive)
        /// </summary>
        /// <param name="endPoint">From endPoint (can be null)</param>
        /// <param name="socketError">Socket error</param>
        Task OnNetworkError(IPEndPoint endPoint, SocketError socketError);

        /// <summary>
        /// Received some data
        /// </summary>
        /// <param name="peer">From peer</param>
        /// <param name="reader">DataReader containing all received data</param>
        /// <param name="channelNumber">Number of channel at which packet arrived</param>
        /// <param name="deliveryMethod">Type of received packet</param>
        Task OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Received unconnected message
        /// </summary>
        /// <param name="remoteEndPoint">From address (IP and Port)</param>
        /// <param name="reader">Message data</param>
        /// <param name="messageType">Message type (simple, discovery request or response)</param>
        Task OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType);

        /// <summary>
        /// Latency information updated
        /// </summary>
        /// <param name="peer">Peer with updated latency</param>
        /// <param name="latency">latency value in milliseconds</param>
        Task OnNetworkLatencyUpdate(NetPeer peer, int latency);

        /// <summary>
        /// On peer connection requested
        /// </summary>
        /// <param name="request">Request information (EndPoint, internal id, additional data)</param>
        Task OnConnectionRequest(ConnectionRequest request);
    }

    public interface IDeliveryEventListener
    {
        /// <summary>
        /// On reliable message delivered
        /// </summary>
        /// <param name="peer"></param>
        /// <param name="userData"></param>
        Task OnMessageDelivered(NetPeer peer, object userData);
    }

    public interface INtpEventListener
    {
        /// <summary>
        /// Ntp response
        /// </summary>
        /// <param name="packet"></param>
        Task OnNtpResponse(NtpPacket packet);
    }

    public interface IPeerAddressChangedListener
    {
        /// <summary>
        /// Called when peer address changed (when AllowPeerAddressChange is enabled)
        /// </summary>
        /// <param name="peer">Peer that changed address (with new address)</param>
        /// <param name="previousAddress">previous IP</param>
        Task OnPeerAddressChanged(NetPeer peer, IPEndPoint previousAddress);
    }

    public class EventBasedNetListener : INetEventListener, IDeliveryEventListener, INtpEventListener, IPeerAddressChangedListener
    {
        public delegate Task OnPeerConnected(NetPeer peer);
        public delegate Task OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo);
        public delegate Task OnNetworkError(IPEndPoint endPoint, SocketError socketError);
        public delegate Task OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);
        public delegate Task OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType);
        public delegate Task OnNetworkLatencyUpdate(NetPeer peer, int latency);
        public delegate Task OnConnectionRequest(ConnectionRequest request);
        public delegate Task OnDeliveryEvent(NetPeer peer, object userData);
        public delegate Task OnNtpResponseEvent(NtpPacket packet);
        public delegate Task OnPeerAddressChangedEvent(NetPeer peer, IPEndPoint previousAddress);

        public event OnPeerConnected PeerConnectedEvent;
        public event OnPeerDisconnected PeerDisconnectedEvent;
        public event OnNetworkError NetworkErrorEvent;
        public event OnNetworkReceive NetworkReceiveEvent;
        public event OnNetworkReceiveUnconnected NetworkReceiveUnconnectedEvent;
        public event OnNetworkLatencyUpdate NetworkLatencyUpdateEvent;
        public event OnConnectionRequest ConnectionRequestEvent;
        public event OnDeliveryEvent DeliveryEvent;
        public event OnNtpResponseEvent NtpResponseEvent;
        public event OnPeerAddressChangedEvent PeerAddressChangedEvent;

        public void ClearPeerConnectedEvent()
        {
            PeerConnectedEvent = null;
        }

        public void ClearPeerDisconnectedEvent()
        {
            PeerDisconnectedEvent = null;
        }

        public void ClearNetworkErrorEvent()
        {
            NetworkErrorEvent = null;
        }

        public void ClearNetworkReceiveEvent()
        {
            NetworkReceiveEvent = null;
        }

        public void ClearNetworkReceiveUnconnectedEvent()
        {
            NetworkReceiveUnconnectedEvent = null;
        }

        public void ClearNetworkLatencyUpdateEvent()
        {
            NetworkLatencyUpdateEvent = null;
        }

        public void ClearConnectionRequestEvent()
        {
            ConnectionRequestEvent = null;
        }

        public void ClearDeliveryEvent()
        {
            DeliveryEvent = null;
        }

        public void ClearNtpResponseEvent()
        {
            NtpResponseEvent = null;
        }

        public void ClearPeerAddressChangedEvent()
        {
            PeerAddressChangedEvent = null;
        }

        Task INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (PeerConnectedEvent != null)
                return PeerConnectedEvent(peer);
            return Task.CompletedTask;
        }

        Task INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (PeerDisconnectedEvent != null)
                return PeerDisconnectedEvent(peer, disconnectInfo);
            return Task.CompletedTask;
        }

        Task INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketErrorCode)
        {
            if (NetworkErrorEvent != null)
                return NetworkErrorEvent(endPoint, socketErrorCode);
            return Task.CompletedTask;
        }

        Task INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (NetworkReceiveEvent != null)
                return NetworkReceiveEvent(peer, reader, channelNumber, deliveryMethod);
            return Task.CompletedTask;
        }

        Task INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            if (NetworkReceiveUnconnectedEvent != null)
                return NetworkReceiveUnconnectedEvent(remoteEndPoint, reader, messageType);
            return Task.CompletedTask;
        }

        Task INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            if (NetworkLatencyUpdateEvent != null)
                return NetworkLatencyUpdateEvent(peer, latency);
            return Task.CompletedTask;
        }

        Task INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (ConnectionRequestEvent != null)
                return ConnectionRequestEvent(request);
            return Task.CompletedTask;
        }

        Task IDeliveryEventListener.OnMessageDelivered(NetPeer peer, object userData)
        {
            if (DeliveryEvent != null)
                return DeliveryEvent(peer, userData);
            return Task.CompletedTask;
        }

        Task INtpEventListener.OnNtpResponse(NtpPacket packet)
        {
            if (NtpResponseEvent != null)
                return NtpResponseEvent(packet);
            return Task.CompletedTask;
        }

        Task IPeerAddressChangedListener.OnPeerAddressChanged(NetPeer peer, IPEndPoint previousAddress)
        {
            if (PeerAddressChangedEvent != null)
                return PeerAddressChangedEvent(peer, previousAddress);
            return Task.CompletedTask;
        }
    }
}
