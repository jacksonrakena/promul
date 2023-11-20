using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Promul.Common.Networking
{
    internal enum ConnectionRequestResult
    {
        None,
        Accept,
        Reject,
        RejectForce
    }

    /// <summary>
    ///     Represents a request by a remote peer to connect.
    /// </summary>
    public class ConnectionRequest
    {
        private readonly NetManager _listener;
        private int _used;

        /// <summary>
        ///     The data sent by the remote peer.
        /// </summary>
        public BinaryReader Data => InternalPacket.Data;

        internal ConnectionRequestResult Result { get; private set; }
        internal NetConnectRequestPacket InternalPacket;

        /// <summary>
        ///     The remote endpoint of the peer.
        /// </summary>
        public readonly IPEndPoint RemoteEndPoint;

        internal void UpdateRequest(NetConnectRequestPacket connectRequest)
        {
            //old request
            if (connectRequest.ConnectionTime < InternalPacket.ConnectionTime)
                return;

            if (connectRequest.ConnectionTime == InternalPacket.ConnectionTime &&
                connectRequest.ConnectionNumber == InternalPacket.ConnectionNumber)
                return;

            InternalPacket = connectRequest;
        }

        private bool TryActivate()
        {
            return Interlocked.CompareExchange(ref _used, 1, 0) == 0;
        }

        internal ConnectionRequest(IPEndPoint remoteEndPoint, NetConnectRequestPacket requestPacket, NetManager listener)
        {
            InternalPacket = requestPacket;
            RemoteEndPoint = remoteEndPoint;
            _listener = listener;
        }

        /// <summary>
        ///     Accepts the connection if the contained data is a <see cref="string"/> and matches <see cref="key"/> exactly.
        /// </summary>
        /// <param name="key">The key to compare the data to.</param>
        /// <returns>Null, if the request was rejected. Otherwise, the connected peer.</returns>
        public async Task<NetPeer?> AcceptIfMatchesKeyAsync(string key)
        {
            if (!TryActivate()) return null;
            try
            {
                if (Data.ReadString() == key)
                    Result = ConnectionRequestResult.Accept;
            }
            catch
            {
                NetDebug.WriteError("[AC] Invalid incoming data");
            }
            if (Result == ConnectionRequestResult.Accept)
                return await _listener.OnConnectionRequestResolved(this, null);

            Result = ConnectionRequestResult.Reject;
            await _listener.OnConnectionRequestResolved(this, null);
            return null;
        }

        /// <summary>
        ///     Accepts the connection.
        /// </summary>
        /// <returns>The connected peer, or null, if the manager was unable to activate the peer.</returns>
        public async Task<NetPeer?> AcceptAsync()
        {
            if (!TryActivate())
                return null;
            Result = ConnectionRequestResult.Accept;
            return await _listener.OnConnectionRequestResolved(this, null);
        }

        /// <summary>
        ///     Rejects the connection, and sends data in response, if provided.
        /// </summary>
        /// <param name="data">The data to send, if provided.</param>
        /// <param name="force">Whether to forcefully disconnect the peer after the rejection.</param>
        public async Task RejectAsync(ArraySegment<byte> data = default, bool force = false)
        {
            if (!TryActivate())
                return;
            Result = force ? ConnectionRequestResult.RejectForce : ConnectionRequestResult.Reject;
            await _listener.OnConnectionRequestResolved(this, data);
        }
    }
}
