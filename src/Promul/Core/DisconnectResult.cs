namespace Promul
{
    /// <summary>
    ///     The result of a disconnection.
    /// </summary>
    internal enum DisconnectResult
    {
        None,

        /// <summary>
        ///     The connection was rejected.
        /// </summary>
        Reject,

        /// <summary>
        ///     The connection was disconnected.
        /// </summary>
        Disconnect
    }
}