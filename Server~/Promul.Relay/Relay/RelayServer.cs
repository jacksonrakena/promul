using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using Promul.Common.Structs;
using Promul.Server.Relay.Sessions;
namespace Promul.Server.Relay;

public class RelayServer : INetEventListener
{
    public NetManager NetManager { get; }

    readonly Dictionary<string, RelaySession> sessions = new Dictionary<string, RelaySession>()
    {
        { "TEST", new RelaySession("TEST") }
    };
    
    readonly Dictionary<NetPeer, RelaySession> sessionsByPeer = new Dictionary<NetPeer, RelaySession>();
    
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

        if (!sessionsByPeer.TryGetValue(peer, out var session))
        {
            if (packet.Type != RelayControlMessageType.Hello)
            {
                Console.WriteLine($"Disconnecting {peer.Id} because they are not attached to a session and they did not send HELLO.");
                peer.Disconnect();
                return;
            }

            var key = Encoding.Default.GetString(packet.Data);
            if (!sessions.TryGetValue(key, out var keyedSession))
            {
                Console.WriteLine($"Disconnecting {peer.Id} because they requested to join a session that does not exist.");
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
        Console.WriteLine($"Connected to {peer.EndPoint}, assigned host");
    }
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Console.WriteLine($"Peer {peer.Id} disconnected: {disconnectInfo.Reason} {disconnectInfo.SocketErrorCode}");
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