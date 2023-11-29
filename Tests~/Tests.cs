using System.Collections;
using Promul.Common.Networking;
using Promul.Common.Networking.Data;
using Promul.Tests.Contexts;

namespace Promul.Tests;

[TestFixture(TestName = "Setup and connection tests")]
[Timeout(DEFAULT_TIMEOUT)]
public class AuthorizationTests
{
    public const int DEFAULT_TIMEOUT = 5000;
    
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
    
    [Test(Description = "Server can be created and bind to port.")]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(10)]
    [TestCase(20)]
    [TestCase(100)]
    [TestCase(500)]
    [TestCase(1000)]
    public async Task Test_Clients_Request_Connection(int nClients)
    {
        var remainingClients = nClients;
        using var m = _managerGroup.GetServer(false);
        m.OnConnectionRequest += async c =>
        {
            Interlocked.Decrement(ref remainingClients);
            await c.AcceptAsync();
        };
        await Task.WhenAll(Enumerable.Range(0, nClients).Select(e => _managerGroup.GetClientStarted()));
        while (remainingClients != 0){}
    }

    [Test(Description = "Server rejects invalid keys.")]
    [TestCase(1)]
    [TestCase(5)]
    public async Task Test_Server_Rejects_Invalid_Key(int nClients)
    {
        var remainingClients = nClients;
        using var server = _managerGroup.GetServer(false);
        
        await Task.WhenAll(Enumerable.Range(0, nClients).Select(e => Task.Run(async () =>
        {
            using var client = await _managerGroup.GetClientStarted(Guid.NewGuid().ToString());
            client.OnPeerDisconnected += async (peer, info) =>
            {
                if (info.Reason == DisconnectReason.ConnectionRejected) Interlocked.Decrement(ref remainingClients);
            };
        })));
        
        while (remainingClients != 0){}
    }

    [Test(Description = "Server sends client correct information.")]
    public async Task Test_Small_Data([Values(1,10,100,1000)] int nDataSize, [Values] DeliveryMethod method)
    {
        var buffer = new byte[nDataSize];
        TestContext.CurrentContext.Random.NextBytes(buffer);
        var passSem = new SemaphoreSlim(1, 1);
        await passSem.WaitAsync();
        using var server = _managerGroup.GetServer(false);
        server.OnReceive += async (peer, reader, chan, receivedMethod) =>
        {
            var bytes = reader.ReadRemainingBytes();
            Console.WriteLine($"== RECEIVED {bytes.Count} (expected {buffer.Length}) on method {receivedMethod:G}, expecting {method:G}) ==");
            var readbuf = bytes.ToArray();
            if (receivedMethod == method && buffer.SequenceEqual(readbuf))
            {
                passSem.Release();
            }
        };
        using var client = await _managerGroup.GetClientStarted();
        client.OnPeerConnected += async p =>
        {
            await p.SendAsync(buffer, method);
        };
        await passSem.WaitAsync();
    }
    
}