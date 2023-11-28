using Promul.Common.Networking;
using Promul.Common.Networking.Data;
using Promul.Common.Structs;
using Promul.Server.Relay.Sessions;

namespace Promul.Server.Relay;

public class RelayServer
{
    public PromulManager PromulManager { get; }

    readonly Dictionary<string, RelaySession> _sessionsByCode = new Dictionary<string, RelaySession>();
    readonly Dictionary<int, RelaySession> _sessionsByPeer = new Dictionary<int, RelaySession>();

    readonly ILogger<RelayServer> _logger;
    readonly ILoggerFactory _factory;
    
    public RelayServer(ILogger<RelayServer> logger, ILoggerFactory factory)
    {
        _logger = logger;
        _factory = factory;
        PromulManager = new PromulManager();


        PromulManager.OnReceive += OnNetworkReceive;
        PromulManager.OnConnectionRequest += OnConnectionRequest;
        PromulManager.OnPeerConnected += OnPeerConnected;
        PromulManager.OnPeerDisconnected += OnPeerDisconnected;
    }

    public Dictionary<string, RelaySession> GetAllSessions() => _sessionsByCode;

    public void CreateSession(string joinCode)
    {
        _sessionsByCode[joinCode] = new RelaySession(joinCode, this, _factory.CreateLogger<RelaySession>());
    }

    public RelaySession? GetSession(string joinCode)
    {
        return _sessionsByCode.GetValueOrDefault(joinCode);
    }
    
    public async Task DestroySession(RelaySession session)
    {
        foreach (var peer in session.Peers)
        {
            _sessionsByPeer.Remove(peer.Id);
        }
        
        await session.DisconnectAll();
        _sessionsByCode.Remove(session.JoinCode);
    }

    public async Task OnNetworkReceive(PromulPeer peer, BinaryReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var packet = reader.ReadRelayControlMessage();

        const string format = "Disconnecting {} ({}) because {}";
        if (!_sessionsByPeer.TryGetValue(peer.Id, out var session))
        {
            _logger.LogInformation(format, peer.Id, peer.EndPoint, "because they are not attached to a session.");
            await PromulManager.DisconnectPeerAsync(peer);
            return;
        }

        await session.OnReceive(peer, packet, deliveryMethod);
    }
    
    public async Task OnConnectionRequest(ConnectionRequest request)
    {
        var joinCode = request.Data.ReadString();
        
        if (!_sessionsByCode.TryGetValue(joinCode, out var keyedSession))
        {
            const string format = "Rejecting {} because {}";
            _logger.LogInformation(format, request.RemoteEndPoint, "because they requested to join a session that does not exist.");
            await request.RejectAsync(force: true);
            return;
        }

        var peer = await request.AcceptAsync();
        await keyedSession.OnJoinAsync(peer);
        _sessionsByPeer[peer.Id] = keyedSession;
    }
    
    public async Task OnPeerConnected(PromulPeer peer)
    {
        _logger.LogInformation($"Connected to {peer.EndPoint}");
    }
    public async Task OnPeerDisconnected(PromulPeer peer, DisconnectInfo disconnectInfo)
    {
        _logger.LogInformation($"Peer {peer.Id} disconnected: {disconnectInfo.Reason} {disconnectInfo.SocketErrorCode}");
        if (_sessionsByPeer.TryGetValue(peer.Id, out var session))
        {
            await session.OnLeave(peer);
            _sessionsByPeer.Remove(peer.Id);
        }
    }
}