using System;

namespace Promul.Common.Structs
{
    /// <summary>
    ///     Represents a protocol-level message sent by a relay participant or server.
    /// </summary>
    public struct RelayControlMessage
    {
        /// <summary>
        ///     The type of this message.
        /// </summary>
        public RelayControlMessageType Type { get; set; }

        /// <summary>
        ///     When being sent from a relay server, this field indicates the author of this message.<br />
        ///     <br />
        ///     Otherwise, this field indicates the desired recipient of this message.
        /// </summary>
        public ulong AuthorClientId { get; set; }

        /// <summary>
        ///     The data enclosed in this message.
        /// </summary>
        public ArraySegment<byte> Data { get; set; }
    }
}