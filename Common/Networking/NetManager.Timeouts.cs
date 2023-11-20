using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Promul.Common.Networking
{
    public partial class NetManager
    {
        private async Task PeerUpdateLoopBlockingAsync(CancellationToken ct = default)
        {
            var peersToRemove = new List<NetPeer>();
            var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ProcessDelayedPackets();
                    var deltaTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
                    startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    async Task tick(NetPeer e)
                    {
                        if (e.ConnectionState == ConnectionState.Disconnected &&
                            e.TimeSinceLastPacket > DisconnectTimeout)
                        {
                            peersToRemove.Add(e);
                        }
                        else
                        {
                            await e.Update(deltaTime);
                        }
                    }

                    await Task.WhenAll(_peersArray.Where(e => e != null).Select(tick));
    
                    if (peersToRemove.Count > 0)
                    {
                        for (int i = 0; i < peersToRemove.Count; i++)
                            RemovePeerInternal(peersToRemove[i]);
                        peersToRemove.Clear();
                    }
    
                    ProcessNtpRequests(deltaTime);
                }
                catch (ThreadAbortException)
                {
                    return;
                }
                catch (Exception e)
                {
                    NetDebug.WriteError("[NM] LogicThread error: " + e);
                }   
            }
        }
    }
}