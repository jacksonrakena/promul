using System.Threading.Tasks;
using Promul.Common.Networking.Packets.Internal;

namespace Promul.Common.Networking
{
    public partial class PromulPeer
    {
        internal async Task<ConnectRequestResult> ProcessConnectRequest(NetConnectRequestPacket connRequest)
        {
            switch (ConnectionState)
            {
                //P2P case
                case ConnectionState.Outgoing:
                    //fast check
                    if (connRequest.ConnectionTime < ConnectTime)
                    {
                        return ConnectRequestResult.P2PLose;
                    }
                    //slow rare case check
                    if (connRequest.ConnectionTime == ConnectTime)
                    {
                        var remoteBytes = EndPoint.Serialize();
                        var localBytes = connRequest.TargetAddress;
                        for (int i = remoteBytes.Size-1; i >= 0; i--)
                        {
                            byte rb = remoteBytes[i];
                            if (rb == localBytes[i])
                                continue;
                            if (rb < localBytes[i])
                                return ConnectRequestResult.P2PLose;
                        }
                    }
                    break;

                case ConnectionState.Connected:
                    //Old connect request
                    if (connRequest.ConnectionTime == ConnectTime)
                    {
                        //just reply accept
                        await PromulManager.SendRaw(_connectAcceptPacket, EndPoint);
                    }
                    //New connect request
                    else if (connRequest.ConnectionTime > ConnectTime)
                    {
                        return ConnectRequestResult.Reconnection;
                    }
                    break;

                case ConnectionState.Disconnected:
                case ConnectionState.ShutdownRequested:
                    if (connRequest.ConnectionTime >= ConnectTime)
                        return ConnectRequestResult.NewConnection;
                    break;
            }
            return ConnectRequestResult.None;
        }
    }
}