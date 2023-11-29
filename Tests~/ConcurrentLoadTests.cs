using Promul.Common.Networking;
using Promul.Common.Networking.Data;
using Promul.Tests.Contexts;

namespace Promul.Tests;

[TestFixture(TestName = "Concurrent load tests")]
[Timeout(DEFAULT_TIMEOUT)]
public class ConcurrentLoadTests
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
    
    [Test(Description = "Tests the server under synthetic load of N_CLIENTS sending connection requests, with a random chance of being valid.")]
    public async Task Test_Connection_Load([Range(1,50)] int nClients)
    {
        var remainingClients = nClients;
        using var server = _managerGroup.GetServer(false);

        foreach (var _ in Enumerable.Range(0, nClients))
        {
            var isValidRequest = TestContext.CurrentContext.Random.NextBool();   
            using var client = await _managerGroup.GetClientStarted(isValidRequest ? _managerGroup.Key : Guid.NewGuid().ToString());
            client.OnPeerDisconnected += async (peer, info) =>
            {
                if (info.Reason == DisconnectReason.ConnectionRejected && !isValidRequest)
                    Interlocked.Decrement(ref remainingClients);
            };
            client.OnPeerConnected += async (peer) =>
            {
                if (isValidRequest) Interlocked.Decrement(ref remainingClients);
            };
        }
        while (remainingClients != 0){}
    }
    
    [Test(Description = "Tests the server under artificial load of N_CLIENTS sending N_DATA_SIZE bytes.")]
    public async Task Test_Data_Load([Range(1,20)] int nClients,
        [Values(500, 1_000, 5_000, 10_000)] int nDataSize)
    {
        var method = DeliveryMethod.ReliableOrdered;
        var buffer = new byte[nDataSize];
        var remainingClients = nClients;
        TestContext.CurrentContext.Random.NextBytes(buffer);
        
        using var server = _managerGroup.GetServer(false);
        server.OnReceive += async (peer, reader, chan, receivedMethod) =>
        {
            var bytes = reader.ReadRemainingBytes();
            Console.WriteLine($"== RECEIVED {bytes.Count} (expected {buffer.Length}) on method {receivedMethod:G}, expecting {method:G}) ==");
            var readbuf = bytes.ToArray();
            if (receivedMethod == method && buffer.SequenceEqual(readbuf))
            {
                Interlocked.Decrement(ref remainingClients);
            }
        };
        foreach (var c in Enumerable.Range(0, nClients))
        {
            using var client = await _managerGroup.GetClientStarted();
            client.OnPeerConnected += async p =>
            {
                await p.SendAsync(buffer, method);
            };
        }
        
        while (remainingClients != 0)
        {
        }
    }
}