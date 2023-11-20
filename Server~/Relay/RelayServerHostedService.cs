using System.Net;
namespace Promul.Server.Relay;

public class RelayServerHostedService : BackgroundService
{
    readonly RelayServer _relayServer;
    readonly ILogger<RelayServerHostedService> _logger;
    public RelayServerHostedService(RelayServer relayServer, ILogger<RelayServerHostedService> logger)
    {
        _relayServer = relayServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _relayServer.NetManager.Ipv6Enabled = false;
        if (_relayServer.NetManager.Bind(IPAddress.Any, IPAddress.Any, 4098))
        {
            _logger.LogInformation($"Listening on port 4098");
            await _relayServer.NetManager.ListenAsync(stoppingToken);
        }
        else _logger.LogError("Failed to start relay server.");
    }
}