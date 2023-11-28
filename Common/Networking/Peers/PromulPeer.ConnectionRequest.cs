using System.Threading.Tasks;
using Promul.Common.Networking.Packets.Internal;

namespace Promul.Common.Networking
{
    public abstract partial class PeerBase
    {
        internal abstract Task<ConnectRequestResult> ProcessConnectionRequestAsync(NetConnectRequestPacket connRequest);
    }
}