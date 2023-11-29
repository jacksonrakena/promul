using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Promul.Common.Networking
{
    public partial class PromulManager
    {
        private async Task PeerUpdateLoopBlockingAsync(CancellationToken ct = default)
        {
            var peersToRemove = new List<PeerBase>();
            var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ProcessDelayedPackets();
                    var deltaTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
                    startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    async Task tick(PeerBase e)
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

                await Task.Delay(1, ct);
            }
        }
    }
}