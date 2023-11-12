using Promul.Relay;
namespace Promul;

public class Program
{
    public static void Main(string[] args)
    {
        var stopping = false;
        var relayServer = new RelayServer();
        relayServer.Start();
        Console.CancelKeyPress += delegate
        {
            stopping = true;
        };
        Console.WriteLine($"Listening on port {relayServer.NetManager.LocalPort}");
        while (!stopping)
        {
            relayServer.NetManager.PollEvents();
            Thread.Sleep(15);
        }
        relayServer.NetManager.Stop();
    }
}