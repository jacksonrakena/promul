using System.Net.Sockets;

namespace Promul.Common.Networking.Data
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
}
