using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Promul.Common.Networking.Data;
using Promul.Common.Structs;
using Promul.Server.Relay;
using Promul.Tests.Contexts;

namespace Promul.Tests;

[Timeout(TestConstants.ExtendedTimeout)]
public class RelayTests
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

    [Test]
    public async Task Test_Clients_Connect_To_Relay([Range(1, 20)] int nRelayClients)
    {
        var sessionId = Guid.NewGuid().ToString();
        var loggerFactory = new LoggerFactory();
        var relayServer = new RelayServer(loggerFactory.CreateLogger<RelayServer>(), loggerFactory);
        relayServer.CreateSession(sessionId);
        
        relayServer.PromulManager.Ipv6Enabled = false;
        if (relayServer.PromulManager.Bind(IPAddress.Any, IPAddress.Any, _managerGroup.ServerPort))
        {
            _ = relayServer.PromulManager.ListenAsync();
        }
        else
        {
            Assert.Fail("Failed to start relay server.");
            return;
        }

        var nWaitingMembers = nRelayClients;
        var host = await _managerGroup.GetClientStarted(sessionId);
        host.OnReceive += async (p, m, c, d) =>
        {
            var control = m.ReadRelayControlMessage();
            if (control.Type == RelayControlMessageType.Connected)
            {
                Interlocked.Decrement(ref nWaitingMembers);
                Console.WriteLine($"== CLIENT {control.AuthorClientId} connected to relay");
            }
        };
        for (int i = 0; i < nRelayClients; i++)
        {
            var cl = await _managerGroup.GetClientStarted(sessionId);
        }
        while (nWaitingMembers != 0)
        {
        }
    }
    
    record RelayPair(int From, int To, byte[] Data) {}

    private RelayPair Generate(int from, int to)
    {
        var buf = new byte[TestContext.CurrentContext.Random.Next(1, 1000)];
        TestContext.CurrentContext.Random.NextBytes(buf);
        return new RelayPair(from, to, buf);
    }
    [Test]
    public async Task Test_Message_Relays([Values(2,3,4,5,6,7,8,9,10,25,50,75,100)] int nRelayClients)
    {
        var nMessages = nRelayClients*2;
        var sessionId = Guid.NewGuid().ToString();
        var loggerFactory = new LoggerFactory();
        var relayServer = new RelayServer(loggerFactory.CreateLogger<RelayServer>(), loggerFactory);
        relayServer.CreateSession(sessionId);
        
        relayServer.PromulManager.Ipv6Enabled = false;
        if (relayServer.PromulManager.Bind(IPAddress.Any, IPAddress.Any, _managerGroup.ServerPort))
        {
            _ = relayServer.PromulManager.ListenAsync();
        }
        else
        {
            Assert.Fail("Failed to start relay server.");
            return;
        }
        var messagePairs = new List<RelayPair>() { };
        for (int i = 0; i < nMessages || messagePairs.Count < 1; i++)
        {
            var from = TestContext.CurrentContext.Random.Next(1, nRelayClients + 1);
            var to = TestContext.CurrentContext.Random.Next(1, nRelayClients + 1);
            if (from == to) continue;
            if (messagePairs.Any(a => a.From == from && a.To == to)) continue;
            messagePairs.Add(Generate(from, to));
        }

        var receivedMessagePairs = new ConcurrentBag<RelayPair>();
        var nReceivedMessagePairs = 0;

        Console.WriteLine("Message pairs:");
        foreach (var mp in messagePairs)
        {
            Console.WriteLine($"{mp.Data.Length} from {mp.From} to {mp.To}");
        }

        var nWaitingMembers = nRelayClients;
        var host = await _managerGroup.GetClientStarted(sessionId);
        host.OnReceive += async (p, m, c, d) =>
        {
            var control = m.ReadRelayControlMessage();
            if (control.Type == RelayControlMessageType.Connected)
            {
                Interlocked.Decrement(ref nWaitingMembers);
                Console.WriteLine($"== HOST SEES CLIENT {control.AuthorClientId} connected to relay");
            }
        };
        for (int i = 0; i < nRelayClients; i++)
        {
            var cl = await _managerGroup.GetClientStarted(sessionId);
            var i1 = i;
            cl.OnReceive += async (p, m, c, d) =>
            {
                var control = m.ReadRelayControlMessage();
                if (control.Type == RelayControlMessageType.Connected)
                {
                    Console.WriteLine($"== CLIENT {i1+1} connected to relay");
                    foreach (var mp in messagePairs)
                    {
                        if (mp.From == i1 + 1)
                        {
                            var cw = CompositeWriter.Create();
                            cw.Write(new RelayControlMessage {Type = RelayControlMessageType.Data, AuthorClientId = (ulong) mp.To, Data = mp.Data });
                            await p.SendAsync(cw);
                        }
                    }
                } else if (control.Type == RelayControlMessageType.Data)
                {
                    foreach (var mp in messagePairs)
                    {
                        if (mp.To == i1 + 1 && mp.From == (int) control.AuthorClientId)
                        {
                            if (mp.Data.SequenceEqual(control.Data))
                            {
                                receivedMessagePairs.Add(mp);
                                Interlocked.Increment(ref nReceivedMessagePairs);
                                Console.WriteLine($"== {mp.To} received {control.Data.Count} from {mp.From} ==");
                                Console.WriteLine($"== Progress: {nReceivedMessagePairs}/{messagePairs.Count}");
                            }
                            else
                            {
                                Console.WriteLine($"== ERROR: In pair {mp.From}->{mp.To}, receiver received {control.Data.Count} bytes instead of expected {mp.Data.Length} bytes!");
                            }
                        }
                    }
                }
            };
        }
        while (nWaitingMembers != 0)
        {
        }
        while (receivedMessagePairs.Count != messagePairs.Count){}
    }
}