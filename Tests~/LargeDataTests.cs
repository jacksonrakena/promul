using Promul.Common.Networking;
using Promul.Tests.Contexts;

namespace Promul.Tests;

[TestFixture(TestName = "Tests with large payloads (1KB - 1MB)")]
[Timeout(DEFAULT_TIMEOUT)]
public class LargeDataTests
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
    
    [Test(Description = "Server sends client correct information, over 1,000 bytes.")]
    [Timeout(10_000)]
    public async Task Test_Large_Data(
        [Values(5_000, 10_000, 15_000, 20_000, 50_000, 100_000)] int nDataSize,
        [Values(DeliveryMethod.ReliableOrdered, DeliveryMethod.ReliableUnordered)] DeliveryMethod method)
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
    
    [Test(Description = "Server sends client correct information, over 100,000 bytes.")]
    [Timeout(10_000)]
    public async Task Test_Very_Large_Data(
        [Values(100_000, 250_000, 500_000, 1_000_000, 10_000_000)] int nDataSize,
        [Values(DeliveryMethod.ReliableOrdered, DeliveryMethod.ReliableUnordered)] DeliveryMethod method)
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