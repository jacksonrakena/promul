using System;

namespace Promul.Common.Networking
{
    /// <summary>
    ///     The connection state of a given peer.
    /// </summary>
    [Flags]
    public enum ConnectionState : byte
    {
        Outgoing         = 1 << 1,
        Connected         = 1 << 2,
        ShutdownRequested = 1 << 3,
        Disconnected      = 1 << 4,
        EndPointChange    = 1 << 5,
        Any = Outgoing | Connected | ShutdownRequested | EndPointChange
    }
}