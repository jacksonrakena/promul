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

    readonly Dictionary<string, RelaySession> _sessionsByCode = new Dictionary<string, RelaySession>();
    readonly Dictionary<int, RelaySession> _sessionsByPeer = new Dictionary<int, RelaySession>();

    readonly ILogger<RelayServer> _logger;
    readonly ILoggerFactory _factory;
    
    public RelayServer(ILogger<RelayServer> logger, ILoggerFactory factory)
    {
        _logger = logger;
        _factory = factory;
        NetManager = new NetManager(this);
    }

    public void CreateSession(string joinCode)
    {
        _sessionsByCode[joinCode] = new RelaySession(joinCode, this, _factory.CreateLogger<RelaySession>());
    }

    public RelaySession? GetSession(string joinCode)
    {
        return _sessionsByCode.GetValueOrDefault(joinCode);
    }
    
    public void DestroySession(RelaySession session)
    {
        foreach (var peer in session.Peers)
        {
            _sessionsByPeer.Remove(peer.Id);
        }
        
        session.DisconnectAll();
        _sessionsByCode.Remove(session.JoinCode);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var packet = reader.Get();

        const string format = "Disconnecting {} ({}) because {}";
        if (!_sessionsByPeer.TryGetValue(peer.Id, out var session))
        {
            _logger.LogInformation(format, peer.Id, peer.EndPoint, "because they are not attached to a session.");
            peer.Disconnect();
            return;
        }

        session.OnReceive(peer, packet, deliveryMethod);
    }
    
    public void OnConnectionRequest(ConnectionRequest request)
    {
        var joinCode = request.Data.GetString();
        
        if (!_sessionsByCode.TryGetValue(joinCode, out var keyedSession))
        {
            const string format = "Rejecting {} because {}";
            _logger.LogInformation(format, request.RemoteEndPoint, "because they requested to join a session that does not exist.");
            request.RejectForce();
            return;
        }

        var peer = request.Accept();
        keyedSession.OnJoin(peer);
        _sessionsByPeer[peer.Id] = keyedSession;
    }
    
    public void OnPeerConnected(NetPeer peer)
    {
        _logger.LogInformation($"Connected to {peer.EndPoint}");
    }
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        _logger.LogInformation($"Peer {peer.Id} disconnected: {disconnectInfo.Reason} {disconnectInfo.SocketErrorCode}");
        if (_sessionsByPeer.TryGetValue(peer.Id, out var session))
        {
            session.OnLeave(peer);
            _sessionsByPeer.Remove(peer.Id);
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