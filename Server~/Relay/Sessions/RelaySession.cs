using Promul.Common.Networking;
using Promul.Common.Networking.Data;
using Promul.Common.Structs;
namespace Promul.Server.Relay.Sessions;

public class RelaySession
{
    readonly Dictionary<int, PeerBase> _connections = new Dictionary<int, PeerBase>();
    int? host = null;
    public PeerBase? HostPeer => host != null ? _connections[host.Value] : null;
    public string JoinCode { get; }

    readonly ILogger<RelaySession> _logger;
    readonly RelayServer _server;

    public RelaySession(string joinCode, RelayServer server, ILogger<RelaySession> logger)
    {
        _logger = logger;
        JoinCode = joinCode;
        _server = server;
    }

    public IEnumerable<PeerBase> Peers => _connections.Values;

    public async Task OnReceive(PeerBase from, RelayControlMessage message, DeliveryMethod method)
    {
        if (!_connections.TryGetValue((int) message.AuthorClientId, out var dest))
        {
            LogInformation($"{from.Id} tried to send information to {message.AuthorClientId}, but {message.AuthorClientId} is not connected to the relay.");
            return;
        }

        switch (message.Type)
        {
            case RelayControlMessageType.Data:
                Console.WriteLine($"{from.Id} requesting to send {message.Data.Count} bytes of data to {message.AuthorClientId}");
                await SendAsync(HostPeer!, new RelayControlMessage { Type = RelayControlMessageType.Data, AuthorClientId = (ulong)from.Id, Data = message.Data }, method);
                break;
            case RelayControlMessageType.KickFromRelay:
                var target = message.AuthorClientId;
                if (from.Id == host)
                {
                    if (_connections.TryGetValue((int)target, out var targetPeer))
                    {
                        _connections.Remove((int)target);
                        await _server.PromulManager.DisconnectPeerAsync(targetPeer);
                        LogInformation($"Host {from.Id} successfully kicked {target}");
                    }
                }
                else LogInformation($"Client {from.Id} tried to illegally kick {target}!");
                break;
            case RelayControlMessageType.Connected:
            case RelayControlMessageType.Disconnected:
            default:
                LogInformation($"Ignoring invalid message {message.Type:G} from {from.Id}");
                break;
        }

    }

    private async Task SendAsync(PeerBase to, RelayControlMessage message, DeliveryMethod method)
    {
        var writer = CompositeWriter.Create();
        writer.Write(message);
        _logger.LogInformation($"Sending {message.Type} from {message.AuthorClientId} to {to.Id}");
        await to.SendAsync(writer, deliveryMethod: method);
    }

    public async Task OnJoinAsync(PeerBase peer)
    {
        _connections[peer.Id] = peer;
        if (host == null)
        {
            host = peer.Id;
            LogInformation($"{peer.Id} has joined and been assigned host.");
        }
        else
        {
            LogInformation($"{peer.Id} has joined");
            
            await SendAsync(HostPeer!, new RelayControlMessage()
            {
                Type = RelayControlMessageType.Connected,
                AuthorClientId = (ulong) peer.Id,
                Data = Array.Empty<byte>()
            }, DeliveryMethod.ReliableOrdered);
            
            await SendAsync(peer, new RelayControlMessage
            {
                Type = RelayControlMessageType.Connected,
                AuthorClientId = (ulong) host!,
                Data = Array.Empty<byte>()
            }, DeliveryMethod.ReliableOrdered);
        }
    }

    public async Task OnLeave(PeerBase peer)
    {
        LogInformation($"{peer.Id} has left");
        if (_connections.ContainsKey(peer.Id))
        {
            _connections.Remove(peer.Id);
            if (host == peer.Id)
            {
                LogInformation("Host has left, resetting");
                host = null;
                await _server.DestroySession(this);
                return;
            }
            if (host != null)
            {
                await SendAsync(HostPeer!, new RelayControlMessage
                {
                    Type = RelayControlMessageType.Disconnected,
                    AuthorClientId = (ulong) peer.Id,
                    Data = Array.Empty<byte>()
                }, DeliveryMethod.ReliableOrdered);
            }   
        }
    }

    public async Task DisconnectAll()
    {
        foreach (var con in _connections.Values)
        {
            await this._server.PromulManager.DisconnectPeerAsync(con);
        }
        _connections.Clear();
    }
    
    private void LogInformation(string message)
    {
        _logger.LogInformation("[{}] {}", this, message);
    }

    public override string ToString() => $"Session {this.JoinCode}";
}