using LiteNetLib;
using LiteNetLib.Utils;
using Promul.Common.Structs;
namespace Promul.Server.Relay.Sessions;

public class RelaySession
{
    readonly Dictionary<int, NetPeer> _connections = new Dictionary<int, NetPeer>();
    int? host = null;
    NetPeer? HostPeer => host != null ? _connections[host.Value] : null;
    public string JoinCode { get; }

    public RelaySession(string joinCode)
    {
        JoinCode = joinCode;
    }

    public void OnReceive(NetPeer from, RelayControlMessage message, DeliveryMethod method)
    {
        if (!_connections.TryGetValue((int) message.AuthorClientId, out var dest))
        {
            Console.WriteLine($"{from.Id} tried to send information to {message.AuthorClientId}, but {message.AuthorClientId} is not connected to the relay.");
            return;
        }

        Send(dest, new RelayControlMessage { Type = RelayControlMessageType.Data, AuthorClientId = (ulong)from.Id, Data = message.Data }, method);
    }

    private void Send(NetPeer to, RelayControlMessage message, DeliveryMethod method)
    {
        var writer = new NetDataWriter();
        writer.Put(message);
        to.Send(writer, method);
    }

    public void OnJoin(NetPeer peer)
    {
        _connections[peer.Id] = peer;
        if (host == null)
        {
            host = peer.Id;
            Console.WriteLine($"[{this}] {peer.Id} has joined and been assigned host.");
        }
        else
        {
            Console.WriteLine($"[{this}] {peer.Id} has joined");
            
            Send(HostPeer!, new RelayControlMessage()
            {
                Type = RelayControlMessageType.ClientConnected,
                AuthorClientId = (ulong) peer.Id,
                Data = Array.Empty<byte>()
            }, DeliveryMethod.ReliableOrdered);
            
            Send(peer, new RelayControlMessage
            {
                Type = RelayControlMessageType.Connected,
                AuthorClientId = (ulong) host!,
                Data = Array.Empty<byte>()
            }, DeliveryMethod.ReliableOrdered);
        }
    }

    public bool OnLeave(NetPeer peer)
    {
        Console.WriteLine($"[{this}] {peer.Id} has left");
        _connections.Remove(peer.Id);
        if (host == peer.Id)
        {
            Console.WriteLine($"[{this}] Host has left, resetting");
            foreach (var con in _connections.Values)
            {
                con.Disconnect();
            }
            _connections.Clear();
            return true;
        }
        
        Send(HostPeer!, new RelayControlMessage
        {
            Type = RelayControlMessageType.ClientDisconnected,
            AuthorClientId = (ulong) peer.Id,
            Data = Array.Empty<byte>()
        }, DeliveryMethod.ReliableOrdered);
        
        return false;
    }

    public override string ToString() => $"Session {this.JoinCode}";
}