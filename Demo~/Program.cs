using System.Net;
using System.Text;
using Promul.Common.Networking;
using Promul.Common.Networking.Data;
using Terminal.Gui;

public enum State
{
    Disconnected,
    Connecting,
    FailedToConnect,
    Connected
}

public class Program : Window
{
    private State state = State.Disconnected;
    private PromulManager manager = new PromulManager();
    private CancellationTokenSource cts = new CancellationTokenSource();

    public void WriteLog(string output)
    {
        logLabel.Text += output + Environment.NewLine;
        logLabel.PositionCursor();
    }

    public void WriteMessage(string msg)
    {
        mLabel.Text += (msg + Environment.NewLine);
        mLabel.PositionCursor();
    }

    public TextView mLabel;
    public TextView logLabel;
    public bool started;
    public Program ()
    {
        Title = "Example App (Ctrl+Q to quit)";

        var messages = new FrameView("Messages")
        {
            Width = Dim.Percent(50),
            Height = Dim.Percent(90)
        };
        mLabel = new TextView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Multiline = true
        };
        messages.Add(mLabel);
        var log = new FrameView("Log")
        {
            Width = Dim.Percent(50),
            Height = Dim.Percent(90),
            X = Pos.Right(messages)
        };
        logLabel = new TextView()
        {
            Width = Dim.Fill(), Height = Dim.Fill(),ReadOnly = true,
            Multiline = true
        };
        log.Add(logLabel);
        var inputFrame = new FrameView("Input")
        {
            Width = Dim.Fill(),
            Height = Dim.Percent(10),
            Y = Pos.Bottom(messages)
        };
        var input = new TextField("")
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        input.KeyPress += args =>
        {
            Task.Run(async () =>
            {
                if (!started && args.KeyEvent.Key == Key.Enter)
                {
                    started = true;
                    await Process(input.Text.ToString());
                    input.Clear();
                }
            });
        };
        inputFrame.Add(input);
        Add(messages,log, inputFrame);

        NetDebug.Logger = new RedirectedLogger(logLabel);
    }

    class RedirectedLogger : INetLogger
    {
        private readonly TextView _output;
        public RedirectedLogger(TextView output)
        {
            _output = output;
        }
        public void WriteNet(NetLogLevel level, string str, params object[] args)
        {
            _output.PositionCursor();
            _output.Text += $"{level:G}: {string.Format(str, args)}" + Environment.NewLine;
        }
    }

    public void StartCommon()
    {
        manager.Ipv6Enabled = false;
        manager.OnPeerConnected += async peer => WriteMessage("Connected to " + peer.Id);
        manager.OnPeerDisconnected += async (peer, reason) => WriteMessage("Disconnected from " + peer.Id + ": " + reason);
        manager.OnNetworkError += async (ep, err) => WriteMessage("Error on " + ep + ": " + err);
        manager.OnConnectionRequest += async req =>
        {
            WriteMessage("Request from " + req.RemoteEndPoint + ", accepting");
            await req.AcceptAsync();
        };
        manager.ConnectionlessMessagesAllowed = true;
        manager.OnConnectionlessReceive +=
            async (point, reader, type) => WriteMessage($"Connectionless receive from " + point);
        manager.OnReceive += async (p, m, ch, dm) =>
        {
            var str = m.ReadString();
            //var data = m.ReadBytes(int.MaxValue);
            WriteMessage(p.Id + ": " +
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
        WriteMessage($"Connected to {peer.Id} ({peer.EndPoint})");
        _ = manager.ListenAsync(cts.Token);
    }

    public async Task Process(string input)
    {
        if (input.StartsWith("host"))
        {
            var port = int.Parse(input.Replace("host ", ""));
            await StartHost(port);
            WriteMessage("Host started on " + port);
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
    public static async Task Main(string[] args)
    {
        Application.Run<Program>();
    }
}