using System.Net;
using Promul.Common.Networking;
using Promul.Common.Networking.Data;

public enum State
{
    Disconnected,
    Connecting,
    FailedToConnect,
    Connected
}

public class Program
{
    private State state = State.Disconnected;
    private PromulManager manager = new PromulManager();
    private CancellationTokenSource cts = new CancellationTokenSource();
    public void PrintStatus()
    {
        Console.WriteLine("Connected to: " + string.Join(",", manager.ConnectedPeerList.Select(e => $"{e.Id} ({e.EndPoint})")));
    }

    public void StartCommon()
    {
        manager.Ipv6Enabled = false;
        manager.OnPeerConnected += async peer => Console.WriteLine("Connected to " + peer.Id);
        manager.OnPeerDisconnected += async (peer, reason) => Console.WriteLine("Disconnected from " + peer.Id + ": " + reason);
        manager.OnNetworkError += async (ep, err) => Console.WriteLine("Error on " + ep + ": " + err);
        manager.OnConnectionRequest += async req =>
        {
            Console.WriteLine("Request from " + req.RemoteEndPoint + ", accepting");
            await req.AcceptAsync();
        };
        manager.ConnectionlessMessagesAllowed = true;
        manager.OnConnectionlessReceive +=
            async (point, reader, type) => Console.WriteLine($"Connectionless receive from " + point);
        manager.OnReceive += async (p, m, ch, dm) =>
        {
            var str = m.ReadString();
            //var data = m.ReadBytes(int.MaxValue);
            Console.WriteLine("Received data from " + p.Id + ": " +
                              /*string.Join(" ", data.Select(e => e.ToString("X"))*/str);
        };

    }
    public async Task StartHost(int port)
    {
        StartCommon();
        manager.Bind(IPAddress.Any, IPAddress.Any, port);
        _ = manager.ListenAsync(cts.Token);
    }

    public async Task StartClient(int port)
    {
        StartCommon();
        manager.Bind(IPAddress.Any, IPAddress.Any, 0);
        var peer = await manager.ConnectAsync(NetUtils.MakeEndPoint("127.0.0.1", port), Array.Empty<byte>());
        Console.WriteLine($"Connected to {peer.Id} ({peer.EndPoint})");
        _ = manager.ListenAsync(cts.Token);
    }

    public async Task Start()
    {
        while (!cts.IsCancellationRequested)
        {
            PrintStatus();
            Console.Write("Your request: ");
    
            string? input = null;
            while (string.IsNullOrWhiteSpace(input)) input = Console.ReadLine();
            if (input.StartsWith("host"))
            {
                var port = int.Parse(input.Replace("host ", ""));
                await StartHost(port);
                Console.WriteLine("Host started on " + port);
            }

            if (input.StartsWith("client"))
            {
                var port = int.Parse(input.Replace("client ", ""));
                await StartClient(port);
            }

            if (input.StartsWith("send"))
            {
                var parts = input.Replace("send ", "").Split(" ");
                var dest = int.Parse(parts[0]);
                var msg = parts[1];
                var wr = CompositeWriter.Create();
                wr.Write(msg);
                await manager.ConnectedPeerList.First(e => e.Id == dest).SendAsync(wr, DeliveryMethod.ReliableOrdered);
            }
        }
    }
    public static async Task Main(string[] args)
    {
        await new Program().Start();
    }
}