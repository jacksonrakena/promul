using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;

namespace Promul.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class SessionController : ControllerBase
{
    private readonly ILogger<SessionController> _logger;

    public SessionController(ILogger<SessionController> logger)
    {
        _logger = logger;
    }
    
    [HttpGet("Create")]
    public SessionCreateInfo CreateSession(int maximumPlayers)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        var sci = new SessionCreateInfo
        {
            JoinCode = new string(Enumerable.Repeat(chars, 6).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray()),
            RelayAddress = "aus628.relays.net.fireworkeyes.com",
            RelayPort = 15593
        };
        
        _logger.LogInformation("User {}:{} created session with join code {}",
            HttpContext.Connection.RemoteIpAddress,
            HttpContext.Connection.RemotePort,
            sci.JoinCode);

        return sci;
    }
}