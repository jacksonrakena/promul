namespace Promul.Relay.Server.Models.Sessions;

public struct SessionInfo
{
    public string JoinCode { get; set; }
    public string RelayAddress { get; set; }
    public int RelayPort { get; set; }
}