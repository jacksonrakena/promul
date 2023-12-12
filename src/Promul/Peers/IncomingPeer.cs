using System.Net;
using System.Threading.Tasks;
namespace Promul
{
    public class IncomingPeer : PeerBase
    {
        private readonly NetworkPacket _connectAcceptPacket;

        internal IncomingPeer(PromulManager promulManager, IPEndPoint remote, int id, int remoteId,
            long connectTime, byte connectionNumber)
            : base(promulManager, remote, id, connectTime, connectionNumber)
        {
            RemoteId = remoteId;
            _connectAcceptPacket = NetConnectAcceptPacket.Make(connectTime, connectionNumber, id);
            ConnectionState = ConnectionState.Connected;
        }

        internal async Task SendAcceptedConnectionAsync()
        {
            await PromulManager.RawSendAsync(_connectAcceptPacket, EndPoint);
        }

        internal override async Task<ConnectRequestResult> ProcessReconnectionRequestAsync(
            NetConnectRequestPacket connRequest)
        {
            switch (ConnectionState)
            {
                case ConnectionState.Outgoing:
                    break;
                case ConnectionState.Connected:
                    if (connRequest.ConnectionTime == ConnectTime)
                        await SendAcceptedConnectionAsync();
                    else if (connRequest.ConnectionTime > ConnectTime) return ConnectRequestResult.Reconnection;
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