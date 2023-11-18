﻿using LiteNetLib;
using LiteNetLib.Utils;
using Promul.Common.Structs;
namespace Promul.Server.Relay.Sessions;

public class RelaySession
{
    readonly Dictionary<int, NetPeer> _connections = new Dictionary<int, NetPeer>();
    int? host = null;
    public NetPeer? HostPeer => host != null ? _connections[host.Value] : null;
    public string JoinCode { get; }

    readonly ILogger<RelaySession> _logger;
    readonly RelayServer _server;

    public RelaySession(string joinCode, RelayServer server, ILogger<RelaySession> logger)
    {
        _logger = logger;
        JoinCode = joinCode;
        _server = server;
    }

    public IEnumerable<NetPeer> Peers => _connections.Values;

    public void OnReceive(NetPeer from, RelayControlMessage message, DeliveryMethod method)
    {
        if (!_connections.TryGetValue((int) message.AuthorClientId, out var dest))
        {
            LogInformation($"{from.Id} tried to send information to {message.AuthorClientId}, but {message.AuthorClientId} is not connected to the relay.");
            return;
        }

        switch (message.Type)
        {
            case RelayControlMessageType.Data:
                Send(dest, new RelayControlMessage { Type = RelayControlMessageType.Data, AuthorClientId = (ulong)from.Id, Data = message.Data }, method);
                break;
            case RelayControlMessageType.KickFromRelay:
                var target = message.AuthorClientId;
                if (from.Id == host)
                {
                    if (_connections.TryGetValue((int)target, out var targetPeer))
                    {
                        _connections.Remove((int)target);
                        targetPeer.Disconnect();
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
            LogInformation($"{peer.Id} has joined and been assigned host.");
        }
        else
        {
            LogInformation($"{peer.Id} has joined");
            
            Send(HostPeer!, new RelayControlMessage()
            {
                Type = RelayControlMessageType.Connected,
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

    public void OnLeave(NetPeer peer)
    {
        LogInformation($"{peer.Id} has left");
        if (_connections.ContainsKey(peer.Id))
        {
            _connections.Remove(peer.Id);
            if (host == peer.Id)
            {
                LogInformation("Host has left, resetting");
                host = null;
                _server.DestroySession(this);
                return;
            }
            if (host != null)
            {
                Send(HostPeer!, new RelayControlMessage
                {
                    Type = RelayControlMessageType.Disconnected,
                    AuthorClientId = (ulong) peer.Id,
                    Data = Array.Empty<byte>()
                }, DeliveryMethod.ReliableOrdered);
            }   
        }
    }

    public void DisconnectAll()
    {
        foreach (var con in _connections.Values)
        {
            con.Disconnect();
        }
        _connections.Clear();
    }
    
    private void LogInformation(string message)
    {
        _logger.LogInformation("[{}] {}", this, message);
    }

    public override string ToString() => $"Session {this.JoinCode}";
}