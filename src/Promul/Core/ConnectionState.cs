using System;
namespace Promul
{
    /// <summary>
    ///     The connection state of a given peer.
    /// </summary>
    [Flags]
    public enum ConnectionState : byte
    {
        /// <summary>
        ///     An outgoing peer awaiting connection acceptance.
        /// </summary>
        Outgoing = 1 << 1,

        /// <summary>
        ///     An outgoing or accepted peer, in a connection accepted state.
        /// </summary>
        Connected = 1 << 2,

        /// <summary>
        ///     Shutdown has been requested.
        /// </summary>
        ShutdownRequested = 1 << 3,
        Disconnected = 1 << 4,
        EndPointChange = 1 << 5,
        Any = Outgoing | Connected | ShutdownRequested | EndPointChange
    }
}