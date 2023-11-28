using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Promul.Common.Networking;
using Promul.Common.Networking.Data;
using Promul.Common.Structs;
using Unity.Netcode;
using UnityEngine;

namespace Promul.Runtime
{
    public class PromulTransport : NetworkTransport
    {
        enum HostType
        {
            None,
            Server,
            Client
        }

        [Tooltip("The port of the relay server.")]
        public ushort Port = 7777;
        [Tooltip("The address of the relay server.")]
        public string Address = "127.0.0.1";

        [Tooltip("Interval between ping packets used for detecting latency and checking connection, in seconds")]
        public float PingInterval = 1f;
        [Tooltip("Maximum duration for a connection to survive without receiving packets, in seconds")]
        public float DisconnectTimeout = 5f;
        [Tooltip("Delay between connection attempts, in seconds")]
        public float ReconnectDelay = 0.5f;
        [Tooltip("Maximum connection attempts before client stops and reports a disconnection")]
        public int MaxConnectAttempts = 10;
        [Tooltip("Size of default buffer for decoding incoming packets, in bytes")]
        public int MessageBufferSize = 1024 * 5;
        [Tooltip("Simulated chance for a packet to be \"lost\", from 0 (no simulation) to 100 percent")]
        public int SimulatePacketLossChance = 0;
        [Tooltip("Simulated minimum additional latency for packets in milliseconds (0 for no simulation)")]
        public int SimulateMinLatency = 0;
        [Tooltip("Simulated maximum additional latency for packets in milliseconds (0 for no simulation")]
        public int SimulateMaxLatency = 0;

        PromulManager _mPromulManager;

        public override ulong ServerClientId => 0;
        HostType m_HostType;

        void OnValidate()
        {
            PingInterval = Math.Max(0, PingInterval);
            DisconnectTimeout = Math.Max(0, DisconnectTimeout);
            ReconnectDelay = Math.Max(0, ReconnectDelay);
            MaxConnectAttempts = Math.Max(0, MaxConnectAttempts);
            MessageBufferSize = Math.Max(0, MessageBufferSize);
            SimulatePacketLossChance = Math.Min(100, Math.Max(0, SimulatePacketLossChance));
            SimulateMinLatency = Math.Max(0, SimulateMinLatency);
            SimulateMaxLatency = Math.Max(SimulateMinLatency, SimulateMaxLatency);
        }

        ConcurrentQueue<(NetworkEvent, ulong, ArraySegment<byte>, float)> _queue = new ConcurrentQueue<(NetworkEvent, ulong, ArraySegment<byte>, float)>();
        
        public override bool IsSupported => Application.platform != RuntimePlatform.WebGLPlayer;

        PromulPeer? _relayPeer;
        CancellationTokenSource _cts = new CancellationTokenSource();

        public async Task SendControl(RelayControlMessage rcm, NetworkDelivery qos)
        {
            var writer = CompositeWriter.Create();
            writer.Write(rcm);
            if (_relayPeer != null) await _relayPeer.SendAsync(writer, ConvertNetworkDelivery(qos));
        }
        
        public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery qos)
        {
            Task.Run(async () =>
            {
                await SendControl(new RelayControlMessage
                {
                    Type = RelayControlMessageType.Data,
                    AuthorClientId = clientId,
                    Data = data
                }, qos);
            });
        }

        async Task OnNetworkReceive(PromulPeer peer, BinaryReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var message = reader.ReadRelayControlMessage();
            var author = message.AuthorClientId;
            Debug.Log($"Receive from {peer.Id} author={author} type={message.Type:G}");
            switch (message.Type)
            {
                // Either we are host and a client has connected,
                // or we're a client and we're connected.
                case RelayControlMessageType.Connected:
                    {
                        _queue.Enqueue((NetworkEvent.Connect, author, default, 0));
                        break;
                    }
                // A client has disconnected from the relay.
                case RelayControlMessageType.Disconnected:
                    {
                        _queue.Enqueue((NetworkEvent.Disconnect, author, default, 0));
                        break;
                    }
                // Relayed data
                case RelayControlMessageType.Data:
                {
                    _queue.Enqueue((NetworkEvent.Data, author, message.Data, 0));
                        break;
                }
                case RelayControlMessageType.KickFromRelay:
                    break;
                default:
                    Debug.LogError("Ignoring Promul control byte " + message.Type);
                    break;
            }
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            clientId = 0;
            receiveTime = Time.realtimeSinceStartup;
            payload = new ArraySegment<byte>();
            if (_queue.TryDequeue(out var i))
            {
                clientId = i.Item2;
                receiveTime = Time.realtimeSinceStartup;
                payload = i.Item3;
                return i.Item1;
            }
            return NetworkEvent.Nothing;
        }

        bool ConnectToRelayServer(string joinCode)
        {
            _ = Task.Run(async () =>
            {
                _mPromulManager.Bind(IPAddress.Any, IPAddress.None, 0);
                var ms = CompositeWriter.Create();
                ms.Write(joinCode);
                _relayPeer = await _mPromulManager.ConnectAsync(NetUtils.MakeEndPoint(Address, Port), ms);
                await _mPromulManager.ListenAsync(_cts.Token);
            }, _cts.Token);
            return true;
        }

        public override bool StartClient()
        {
            m_HostType = HostType.Client;
            return ConnectToRelayServer("TEST");
        }

        public override bool StartServer()
        {
            m_HostType = HostType.Server;
            return ConnectToRelayServer("TEST");
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            SendControl(new RelayControlMessage {Type = RelayControlMessageType.KickFromRelay, AuthorClientId = clientId, Data = Array.Empty<byte>() }, NetworkDelivery.Reliable);
        }

        public override void DisconnectLocalClient()
        {
            _ = Task.Run(() => _mPromulManager.DisconnectAllPeersAsync());
            _relayPeer = null;
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            if (_relayPeer != null) return (ulong)_relayPeer.Ping * 2;
            return 0;
        }

        public override void Shutdown()
        {
            Debug.Log("Shutdown");
            _mPromulManager.OnConnectionRequest -= OnConnectionRequest;
            _mPromulManager.OnPeerDisconnected -= OnPeerDisconnected;
            _mPromulManager.OnReceive -= OnNetworkReceive;
            _ = _mPromulManager.StopAsync();
            
            _cts.Cancel();
            _relayPeer = null;
            m_HostType = HostType.None;
        }

        public override void Initialize(NetworkManager? networkManager = null)
        {
            _mPromulManager = new PromulManager
            {
                PingInterval = SecondsToMilliseconds(PingInterval),
                DisconnectTimeout = SecondsToMilliseconds(DisconnectTimeout),
                ReconnectDelay = SecondsToMilliseconds(ReconnectDelay),
                MaximumConnectionAttempts = MaxConnectAttempts,
                SimulatePacketLoss = SimulatePacketLossChance > 0,
                SimulatePacketLossChance = SimulatePacketLossChance,
                SimulateLatency = SimulateMaxLatency > 0,
                SimulationMinLatency = SimulateMinLatency,
                SimulationMaxLatency = SimulateMaxLatency,
                Ipv6Enabled = false
            };

            _mPromulManager.OnConnectionRequest += OnConnectionRequest;
            _mPromulManager.OnPeerDisconnected += OnPeerDisconnected;
            _mPromulManager.OnReceive += OnNetworkReceive;
        }

        static DeliveryMethod ConvertNetworkDelivery(NetworkDelivery type)
        {
            return type switch
            {
                NetworkDelivery.Unreliable => DeliveryMethod.Unreliable,
                NetworkDelivery.UnreliableSequenced => DeliveryMethod.Sequenced,
                NetworkDelivery.Reliable => DeliveryMethod.ReliableUnordered,
                NetworkDelivery.ReliableSequenced => DeliveryMethod.ReliableOrdered,
                NetworkDelivery.ReliableFragmentedSequenced => DeliveryMethod.ReliableOrdered,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
        }
        async Task OnConnectionRequest(ConnectionRequest request)
        {
            await request.RejectAsync(force: true);
        }
        async Task OnPeerDisconnected(PromulPeer peer, DisconnectInfo disconnectInfo)
        {
            Debug.Log("Disconnected " + disconnectInfo.Reason.ToString() + " " + disconnectInfo.SocketErrorCode.ToString());
            if (disconnectInfo.Reason != DisconnectReason.DisconnectPeerCalled) 
                _queue.Enqueue((NetworkEvent.TransportFailure, 0, new ArraySegment<byte>(), 0));
        }

        static int SecondsToMilliseconds(float seconds)
        {
            return (int)Mathf.Ceil(seconds * 1000);
        }
    }
}