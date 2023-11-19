using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
namespace LiteNetLib
{
    public partial class NetManager
    {
        private async Task DisconnectIdlers()
        {
            var peersToRemove = new List<NetPeer>();
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            
            try
            {
                ProcessDelayedPackets();
                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                elapsed = elapsed <= 0 ? 1 : elapsed;
                stopwatch.Restart();
    
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                {
                    if (netPeer.ConnectionState == ConnectionState.Disconnected &&
                        netPeer.TimeSinceLastPacket > DisconnectTimeout)
                    {
                        peersToRemove.Add(netPeer);
                    }
                    else
                    {
                        await netPeer.Update(elapsed);
                    }
                }
    
                if (peersToRemove.Count > 0)
                {
                    _peersLock.EnterWriteLock();
                    for (int i = 0; i < peersToRemove.Count; i++)
                        RemovePeerInternal(peersToRemove[i]);
                    _peersLock.ExitWriteLock();
                    peersToRemove.Clear();
                }
    
                ProcessNtpRequests(elapsed);
            }
            catch (ThreadAbortException)
            {
                return;
            }
            catch (Exception e)
            {
                NetDebug.WriteError("[NM] LogicThread error: " + e);
            }
            
            stopwatch.Stop();
        }
    }
}