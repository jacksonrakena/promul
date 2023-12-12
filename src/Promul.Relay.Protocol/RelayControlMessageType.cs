namespace Promul.Relay.Protocol
{
    public enum RelayControlMessageType : byte
    {
        /// <summary>
        ///     Relay &#8594; Member: A member of the relay wishes to send data to you.<br /><br />
        ///     Member &#8594; Relay: This member wishes to send data to the member specified in
        ///     <see cref="RelayControlMessage.AuthorClientId" />.
        /// </summary>
        Data = 0x00,

        /// <summary>
        ///     Relay &#8594; Host: A client has connected to the relay.<br /><br />
        ///     Relay &#8594; Client: You are connected to the relay.
        /// </summary>
        Connected = 0x10,

        /// <summary>
        ///     Relay &#8594; Host: A client has disconnected from the relay.
        /// </summary>
        Disconnected = 0x12,

        /// <summary>
        ///     Host &#8594; Relay: Requesting the relay disconnect a member of the relay.
        /// </summary>
        KickFromRelay = 0x01
    }
}