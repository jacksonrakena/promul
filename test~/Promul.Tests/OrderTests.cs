using Promul.Tests.Contexts;
namespace Promul.Tests;

[Timeout(TestConstants.ExtendedTimeout)]
public class OrderTests
{
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
    
    [Test(Description = "Server receives information in correct order.")]
    public async Task Test_Correct_Order(
        [Values(DeliveryMethod.ReliableOrdered)] 
        DeliveryMethod m)
    {
        var nMessageCount = 300;
        var messages = Enumerable.Range(0, nMessageCount).Select(_ =>
        {
            var buf = new byte[TestContext.CurrentContext.Random.Next(1, 1000)];
            TestContext.CurrentContext.Random.NextBytes(buf);
            return buf;
        }).ToList();

        var receivedMessages = new Dictionary<int, List<byte[]>>();
        var remainingClients = 10;
        
        using var server = _managerGroup.GetServer(false);
        server.OnReceive += async (peer, reader, chan, receivedMethod) =>
        {
            if (receivedMethod != m) return;
            if (!receivedMessages.ContainsKey(peer.Id)) receivedMessages.Add(peer.Id, new List<byte[]>());
            var bytes = reader.ReadRemainingBytes();
            receivedMessages[peer.Id].Add(bytes.ToArray());
            if (receivedMessages[peer.Id].Count == messages.Count)
            {
                var match = true;
                for (int v = 0; v < messages.Count; v++)
                {
                    if (!receivedMessages[peer.Id][v].SequenceEqual(messages[v]))
                    {
                        match = false;
                    }
                }
                if (!match)
                {
                    Console.WriteLine($"== CLIENT {peer.Id} FAILED, MESSAGES OUT OF ORDER");
                    Console.WriteLine("Expected order:");
                    for (int i = 0; i < messages.Count; i++)
                    {
                        Console.WriteLine($"{i+1}: size {messages[i].Length}");
                    }
                    Console.WriteLine("Received order: ");
                    for (int i = 0; i < receivedMessages[peer.Id].Count; i++)
                    {
                        Console.WriteLine($"{i+1}: size {receivedMessages[peer.Id][i].Length}");
                    }
                }
                else
                {
                    Interlocked.Decrement(ref remainingClients);
                    Console.WriteLine($"== CLIENT {peer.Id} FINISHED {messages.Count} IN ORDER");
                }
            } else Console.WriteLine($"== CLIENT {peer.Id} RECEIVED {receivedMessages[peer.Id].Count}, WAITING FOR {messages.Count-receivedMessages[peer.Id].Count}");
        };
        for (int i = 0; i < remainingClients; i++)
        {
            var client = await _managerGroup.GetClientStarted();
            client.OnPeerConnected += async p =>
            {
                foreach (var message in messages)
                {
                    await p.SendAsync(message, m);
                }
            };   
        }

        while (remainingClients != 0)
        {
        }
    }
}