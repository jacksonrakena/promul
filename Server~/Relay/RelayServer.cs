using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using Promul.Common.Structs;
using Promul.Server.Relay.Sessions;
namespace Promul.Server.Relay;

public class RelayServer : INetEventListener
{
    public NetManager NetManager { get; }

    readonly Dictionary<string, RelaySession> sessions = new Dictionary<string, RelaySession>();
    
    readonly Dictionary<NetPeer, RelaySession> sessionsByPeer = new Dictionary<NetPeer, RelaySession>();

    readonly ILogger<RelayServer> _logger;
    readonly ILoggerFactory _factory;
    
    public RelayServer(ILogger<RelayServer> logger, ILoggerFactory factory)
    {
        _logger = logger;
        _factory = factory;
        NetManager = new NetManager(this)
        {
            PingInterval = 1000,
            //ReconnectDelay = 500,
            DisconnectTimeout = 10000
        };
    }

    public void CreateSession(string joinCode)
    {
        sessions[joinCode] = new RelaySession(joinCode, _factory.CreateLogger<RelaySession>());
    }

    public RelaySession? GetSession(string joinCode)
    {
        return sessions.GetValueOrDefault(joinCode);
    }

    public void Start()
    {
        NetManager.IPv6Enabled = false;
        NetManager.Start(IPAddress.Any, IPAddress.Any, 4098);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var packet = reader.Get();

        const string format = "Disconnecting {} ({}) because {}";
        if (!sessionsByPeer.TryGetValue(peer, out var session))
        {
            if (packet.Type != RelayControlMessageType.Hello)
            {
                _logger.LogInformation(format, peer.Id, peer.EndPoint, "because they are not attached to a session and they did not send HELLO.");
                peer.Disconnect();
                return;
            }

            var key = Encoding.Default.GetString(packet.Data);
            if (!sessions.TryGetValue(key, out var keyedSession))
            {
                _logger.LogInformation(format, peer.Id, peer.EndPoint, "because they requested to join a session that does not exist.");
                peer.Disconnect();
                return;
            }
            keyedSession.OnJoin(peer);
            sessionsByPeer[peer] = keyedSession;
            
            return;
        }

        session.OnReceive(peer, packet, deliveryMethod);
    }
    
    public void OnConnectionRequest(ConnectionRequest request)
    {
        request.Accept();
    }
    
    public void OnPeerConnected(NetPeer peer)
    {
        _logger.LogInformation($"Connected to {peer.EndPoint}");
    }
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        _logger.LogInformation($"Peer {peer.Id} disconnected: {disconnectInfo.Reason} {disconnectInfo.SocketErrorCode}");
        if (sessionsByPeer.TryGetValue(peer, out var session))
        {
            session.OnLeave(peer);
            sessionsByPeer.Remove(peer);
        }
    }
    
    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
    }
    
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }
    
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }
    
}