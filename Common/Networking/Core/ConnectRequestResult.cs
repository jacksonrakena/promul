namespace Promul.Common.Networking
{
    /// <summary>
    ///     The result of a connection request.
    /// </summary>
    internal enum ConnectRequestResult
    {
        None,
        
        P2PLose,
        
        /// <summary>
        ///     The remote peer was previously connected, so this connection is a reconnection.
        /// </summary>
        Reconnection,
        
        /// <summary>
        ///     The remote peer was disconnected, so this connection is new.
        /// </summary>
        NewConnection
    }
}