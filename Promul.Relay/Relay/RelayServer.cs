using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Promul.Common.Structs;
namespace Promul.Server.Relay;

public class RelayServer : INetEventListener
{
    public NetManager NetManager { get; }
    readonly Dictionary<int, NetPeer> connections = new Dictionary<int, NetPeer>();
    
    public RelayServer()
    {
        NetManager = new NetManager(this)
        {
            PingInterval = 1000,
            //ReconnectDelay = 500,
            DisconnectTimeout = 10000
        };
    }

    public void Start()
    {
        NetManager.IPv6Enabled = false;
        NetManager.Start(IPAddress.Any, IPAddress.Any, 4098);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var packet = reader.Get();
        if (packet.Type == RelayControlMessageType.Data)
        {
            if (!connections.TryGetValue((int) packet.AuthorClientId, out var dest))
            {
                Console.WriteLine($"{peer.Id} tried to send information to {packet.AuthorClientId}, but {packet.AuthorClientId} is not connected to the relay.");
                return;
            }
            
            var writer = new NetDataWriter();
            var msg = new RelayControlMessage
            {
                Type = RelayControlMessageType.Data,
                AuthorClientId = (ulong)peer.Id,
                Data = packet.Data
            };
            writer.Put(msg);
            dest.Send(writer, deliveryMethod);
        }
    }
    
    public void OnConnectionRequest(ConnectionRequest request)
    {
        request.Accept();
    }
    
    public void OnPeerConnected(NetPeer peer)
    {
        if (connections.Count == 0)
        {
            Console.WriteLine($"Connected to {peer.EndPoint}, assigned host");
        }
        else
        {
            var hostWriter = new NetDataWriter();
            hostWriter.Put(new RelayControlMessage
            {
                Type = RelayControlMessageType.ClientConnected,
                AuthorClientId = (ulong) peer.Id,
                Data = Array.Empty<byte>()
            });
            var host = connections[0];
            host.Send(hostWriter, DeliveryMethod.ReliableOrdered);
            Console.WriteLine($"Told host {host.Id} that {peer.Id} wishes to join relay.");
                
            var responseMessage = new NetDataWriter();
            responseMessage.Put(new RelayControlMessage
            {
                Type = RelayControlMessageType.Connected,
                AuthorClientId = (ulong)host.Id,
                Data = Array.Empty<byte>()
            });
            peer.Send(responseMessage, DeliveryMethod.ReliableOrdered);
            Console.WriteLine($"Told client {peer.Id} that they are ready to join.");
        }
        connections[peer.Id] = peer;
    }
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Console.WriteLine($"Peer {peer.Id} disconnected: {disconnectInfo.Reason} {disconnectInfo.SocketErrorCode}");
        if (peer.Id == 0)
        {
            Console.WriteLine("Host disconnecting. Resetting state");
            foreach (var con in connections.Values)
            {
                con.Disconnect();
            }
            connections.Clear();
        }
        else
        {
            if (connections.TryGetValue(0, out var host))
            {
                var message = new NetDataWriter();
                message.Put(new RelayControlMessage
                {
                    Type = RelayControlMessageType.ClientDisconnected,
                    AuthorClientId = (ulong) peer.Id,
                    Data = Array.Empty<byte>()
                });
                host.Send(message, DeliveryMethod.ReliableOrdered);
            }
            connections.Remove(peer.Id);
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