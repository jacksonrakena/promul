namespace Promul.Api;

public struct SessionCreateInfo
{
    public string JoinCode { get; set; }
    public string RelayAddress { get; set; }
    public int RelayPort { get; set; }
}