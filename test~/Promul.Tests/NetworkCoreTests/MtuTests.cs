using Promul.Common.Networking;
using Promul.Tests.Contexts;

namespace Promul.Tests;

[Timeout(TestConstants.ExtendedTimeout)]
public class MtuTests
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
    public async Task Test_Negotiates_Mtu_Correctly()
    {
        using var server = _managerGroup.GetServer(false);
        using var client = await _managerGroup.GetClientStarted();
        
        
        while (server.FirstPeer?.MaximumTransferUnit != NetConstants.PossibleMtu[^1] &&
               client.FirstPeer?.MaximumTransferUnit != NetConstants.PossibleMtu[^1])
        {
            
        }
    }
}