using System.Net;

namespace Promul.Server.Relay;

public class RelayServerHostedService : BackgroundService
{
    private readonly ILogger<RelayServerHostedService> _logger;
    private readonly RelayServer _relayServer;

    public RelayServerHostedService(RelayServer relayServer, ILogger<RelayServerHostedService> logger)
    {
        _relayServer = relayServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _relayServer.PromulManager.Ipv6Enabled = false;
        if (_relayServer.PromulManager.Bind(IPAddress.Any, IPAddress.Any, 4098))
        {
            _logger.LogInformation("Listening on port 4098");
            await _relayServer.PromulManager.ListenAsync(stoppingToken);
        }
        else
        {
            _logger.LogError("Failed to start relay server.");
            return;
        }

        await _relayServer.PromulManager.StopAsync();
    }
}