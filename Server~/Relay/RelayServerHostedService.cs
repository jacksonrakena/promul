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
        _relayServer.NetManager.IPv6Enabled = false;
        _relayServer.NetManager.Start(IPAddress.Any, IPAddress.Any, 4098);
        _logger.LogInformation($"Listening on port {_relayServer.NetManager.LocalPort}");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            _relayServer.NetManager.PollEvents();
            await Task.Delay(15, stoppingToken);
        }
    }
}