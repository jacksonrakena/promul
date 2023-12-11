using System.Net;
using System.Text;
using Promul.Common.Networking;
using Promul.Common.Networking.Data;

namespace Promul.Tests.Contexts;

public class ManagerGroup : IDisposable
{
    public readonly string Key = Guid.NewGuid().ToString();
    public readonly int ServerPort = (int) TestContext.CurrentContext.Random.NextUInt(3000, 9000);

    private readonly Dictionary<int, ManagerTestable> _managers = new Dictionary<int, ManagerTestable>();

    public ManagerTestable GetServer(bool ipv6Enabled = false)
    {
        if (_managers.TryGetValue(0, out var m))
            throw new InvalidOperationException("Cannot request server after server has been made");
        var manager = new ManagerTestable(0, this, new CancellationTokenSource());
        manager.Ipv6Enabled = ipv6Enabled;
        manager.Bind(IPAddress.Any, IPAddress.Any, ServerPort);
        manager.OnConnectionRequest += async (req) => { await req.AcceptIfMatchesKeyAsync(Key); };
        _ = manager.ListenAsync(manager.Cts.Token);
        _managers[0] = manager;
        return manager;
    }

    public async Task<ManagerTestable> GetClientStarted(string serverKey = "", bool ipv6Enabled = false)
    {
        var key = _managers.Keys.MaxBy(a => a) + 1;
        var manager = new ManagerTestable(key, this, new CancellationTokenSource());
        manager.Ipv6Enabled = ipv6Enabled;
        manager.Bind(IPAddress.Any, IPAddress.Any, 0);
        _ = manager.ListenAsync(manager.Cts.Token);
        var cw = CompositeWriter.Create();
        cw.Write(string.IsNullOrEmpty(serverKey) ? Key : serverKey);
        _ = manager.ConnectAsync(NetUtils.MakeEndPoint("127.0.0.1", ServerPort), cw);
        _managers[key] = manager;
        return manager;
    }

    public void ChildDispose(int key)
    {
        
    }
    
    public void Dispose()
    {
        foreach (var (k, m) in _managers)
        {
            m.Cts.Cancel();
            _ = m.StopAsync();
        }
    }
}