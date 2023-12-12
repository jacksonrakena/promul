using Promul.Tests.Contexts;
namespace Promul.Tests;

[Timeout(DEFAULT_TIMEOUT)]
public class ShutdownTests
{
    public const int DEFAULT_TIMEOUT = 7000;
    
    private ManagerGroup _managerGroup;
    [SetUp]
    public void Setup()
    {
        _managerGroup = new ManagerGroup();
    }

    [TearDown]
    public void Teardown()
    {
        _managerGroup.Dispose();
    }

    [Test]
    public async Task Test_Shutdown_Grace()
    {
        var testData = "this is a test";
        var sem = new SemaphoreSlim(1, 1);
        await sem.WaitAsync();
        using var server = _managerGroup.GetServer(false);
        server.OnPeerConnected += async p =>
        {
            var cw = CompositeWriter.Create();
            cw.Write(testData);
            await server.DisconnectPeerAsync(p, cw);
        };
        using var client = await _managerGroup.GetClientStarted();
        client.OnPeerDisconnected += async (p, d) =>
        {
            var data = d.AdditionalData!.ReadString();
            Console.WriteLine($"Client rejected with text {data} (expected {testData})");
            if (data.Equals(testData)) sem.Release();
        };

        await sem.WaitAsync();
    }
    
    [Test]
    public async Task Test_Shutdown_Force()
    {
        var testData = "this is a test";
        var sem = new SemaphoreSlim(1, 1);
        await sem.WaitAsync();
        using var server = _managerGroup.GetServer(false);
        server.OnPeerConnected += async p =>
        {
            var cw = CompositeWriter.Create();
            cw.Write(testData);
            await server.ForceDisconnectPeerAsync(p);
        };
        using var client = await _managerGroup.GetClientStarted();
        client.OnPeerDisconnected += async (p, d) =>
        {
            if (d.AdditionalData == null && d.Reason == DisconnectReason.Timeout) sem.Release();
        };

        await sem.WaitAsync();
    }

    [Test]
    [TestOf(nameof(PromulManager.DisconnectAllPeersAsync))]
    public async Task Test_Disconnect_All_Peers([Range(1,10)] int nClients)
    {
        var remainingClients = nClients;
        var remainingUp = 0;
        using var server = _managerGroup.GetServer(false);
        server.OnPeerConnected += async p =>
        {
            Interlocked.Increment(ref remainingUp);
        };
        foreach (var c in Enumerable.Range(0, nClients))
        {
            var client = await _managerGroup.GetClientStarted();
            client.OnPeerDisconnected += async (p, d) =>
            {
                Interlocked.Decrement(ref remainingClients);
            };
        }
        while (remainingUp != nClients) {}
        await server.DisconnectAllPeersAsync();
        while (remainingClients != 0)
        {
        }
    }
}