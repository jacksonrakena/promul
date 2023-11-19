using System.Runtime.InteropServices;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LiteNetLib
{
    public partial class NetManager
    {
        private Socket? _udpSocketv4;
        private Socket? _udpSocketv6;
#if UNITY_2018_3_OR_NEWER
        private PausedSocketFix _pausedSocketFix;
#endif

#if !LITENETLIB_UNSAFE
#endif

        private const int SioUdpConnreset = -1744830452; //SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12
        private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff02::1");
        public static readonly bool IPv6Support;

        // special case in iOS (and possibly android that should be resolved in unity)
        internal bool NotConnected;

        public short Ttl
        {
            get
            {
#if UNITY_SWITCH
                return 0;
#else
                return _udpSocketv4?.Ttl ?? 0;
#endif
            }
            internal set
            {
#if !UNITY_SWITCH
                if (_udpSocketv4 != null) _udpSocketv4!.Ttl = value;
#endif
            }
        }

        static NetManager()
        {
#if DISABLE_IPV6
            IPv6Support = false;
#elif !UNITY_2019_1_OR_NEWER && !UNITY_2018_4_OR_NEWER && (!UNITY_EDITOR && ENABLE_IL2CPP)
            string version = UnityEngine.Application.unityVersion;
            IPv6Support = Socket.OSSupportsIPv6 && int.Parse(version.Remove(version.IndexOf('f')).Split('.')[2]) >= 6;
#else
            IPv6Support = Socket.OSSupportsIPv6;
#endif
        }

        private async Task<bool> ProcessError(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.NotConnected:
                    NotConnected = true;
                    return true;
                case SocketError.Interrupted:
                case SocketError.NotSocket:
                case SocketError.OperationAborted:
                    return true;
                case SocketError.ConnectionReset:
                case SocketError.MessageSize:
                case SocketError.TimedOut:
                case SocketError.NetworkReset:
                    //NetDebug.Write($"[R]Ignored error: {(int)ex.SocketErrorCode} - {ex}");
                    break;
                default:
                    NetDebug.WriteError($"[R]Error code: {(int)ex.SocketErrorCode} - {ex}");
                    if (OnNetworkError != null) await OnNetworkError(null, ex.SocketErrorCode);
                    break;
            }
            return false;
        }
        
        private async Task ReceiveInternalAsync(Socket s, EndPoint bufferEndPoint, CancellationToken ct = default)
        {
            var packet = PoolGetPacket(NetConstants.MaxPacketSize);
            var receive = await s.ReceiveFromAsync(packet.RawData, SocketFlags.None, bufferEndPoint);
            packet.Size = receive.ReceivedBytes;
            await OnMessageReceived(packet, (IPEndPoint) receive.RemoteEndPoint);
        }

        /// <summary>
        /// Begins listening on all available and configured interfaces.
        /// This method will block until the <see cref="CancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to stop the listen operation.</param>
        public async Task ListenAsync(CancellationToken cancellationToken = default)
        {
            EndPoint bufferEndPoint4 = new IPEndPoint(IPAddress.Any, 0);
            EndPoint bufferEndPoint6 = new IPEndPoint(IPAddress.IPv6Any, 0);

            _ = Task.Run(() => PeerUpdateLoopBlockingAsync(cancellationToken));
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_udpSocketv6 == null)
                    {
                        await ReceiveInternalAsync(_udpSocketv4, bufferEndPoint4, cancellationToken);
                    }
                    else
                    {
                        await Task.WhenAll(
                            ReceiveInternalAsync(_udpSocketv4, bufferEndPoint4, cancellationToken), 
                            ReceiveInternalAsync(_udpSocketv6, bufferEndPoint6, cancellationToken)
                        );
                    }
                }
                catch (SocketException ex)
                {
                    if (await ProcessError(ex))
                        return;
                }
                catch (ObjectDisposedException)
                {
                    //socket closed
                    return;
                }
                catch (ThreadAbortException)
                {
                    //thread closed
                    return;
                }
                catch (Exception e)
                {
                    //protects socket receive thread
                    NetDebug.WriteError("[NM] SocketReceiveThread error: " + e );
                }
            }
        }

        /// <summary>
        /// Binds the service to both IP addresses specified.
        /// </summary>
        /// <param name="addressIPv4">The IPv4 address to bind to.</param>
        /// <param name="addressIPv6">The IPv6 address to bind to.</param>
        /// <param name="port">The port to bind to.</param>
        /// <returns>Whether the service was successfully able to bind to both sockets.</returns>
        public bool Bind(IPAddress addressIPv4, IPAddress addressIPv6, int port)
        {
            if (NotConnected)
                return false;

            NotConnected = false;
            _udpSocketv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (!BindInternal(_udpSocketv4, new IPEndPoint(addressIPv4, port)))
                return false;

            LocalPort = ((IPEndPoint) _udpSocketv4.LocalEndPoint).Port;

#if UNITY_2018_3_OR_NEWER
            if (_pausedSocketFix == null)
                _pausedSocketFix = new PausedSocketFix(this, addressIPv4, addressIPv6, port, false);
#endif
            
            //Check IPv6 support
            if (IPv6Support && IPv6Enabled)
            {
                _udpSocketv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                //Use one port for two sockets
                if (BindInternal(_udpSocketv6, new IPEndPoint(addressIPv6, LocalPort)))
                {
                }
                else
                {
                    _udpSocketv6 = null;
                }
            }
            
            return true;
        }

        private bool BindInternal(Socket socket, IPEndPoint ep)
        {
            //Setup socket
            socket.ReceiveTimeout = 500;
            socket.SendTimeout = 500;
            socket.ReceiveBufferSize = NetConstants.SocketBufferSize;
            socket.SendBufferSize = NetConstants.SocketBufferSize;
            socket.Blocking = true;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    socket.IOControl(SioUdpConnreset, new byte[] {0}, null);
                }
                catch
                {
                    //ignored
                }
            }

            try
            {
                socket.ExclusiveAddressUse = !ReuseAddress;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, ReuseAddress);
            }
            catch
            {
                //Unity with IL2CPP throws an exception here, it doesn't matter in most cases so just ignore it
            }
            if (ep.AddressFamily == AddressFamily.InterNetwork)
            {
                Ttl = NetConstants.SocketTTL;

                try { socket.EnableBroadcast = true; }
                catch (SocketException e)
                {
                    NetDebug.WriteError($"[B]Broadcast error: {e.SocketErrorCode}");
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // try { socket.DontFragment = true; }
                    // catch (SocketException e)
                    // {
                    //     NetDebug.WriteError($"[B]DontFragment error: {e.SocketErrorCode}");
                    // }
                }
            }
            //Bind
            try
            {
                socket.Bind(ep);
                NetDebug.Write(NetLogLevel.Trace, $"[B]Successfully binded to port: {((IPEndPoint)socket.LocalEndPoint).Port}, AF: {socket.AddressFamily}");

                //join multicast
                if (ep.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    try
                    {
#if !UNITY_2018_3_OR_NEWER
                        socket.SetSocketOption(
                            SocketOptionLevel.IPv6,
                            SocketOptionName.AddMembership,
                            new IPv6MulticastOption(MulticastAddressV6));
#endif
                    }
                    catch (Exception)
                    {
                        // Unity3d throws exception - ignored
                    }
                }
            }
            catch (SocketException bindException)
            {
                switch (bindException.SocketErrorCode)
                {
                    //IPv6 bind fix
                    case SocketError.AddressAlreadyInUse:
                        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            try
                            {
                                //Set IPv6Only
                                socket.DualMode = false;
                                socket.Bind(ep);
                            }
                            catch (SocketException ex)
                            {
                                //because its fixed in 2018_3
                                NetDebug.WriteError($"[B]Bind exception: {ex}, errorCode: {ex.SocketErrorCode}");
                                return false;
                            }
                            return true;
                        }
                        break;
                    //hack for iOS (Unity3D)
                    case SocketError.AddressFamilyNotSupported:
                        return true;
                }
                NetDebug.WriteError($"[B]Bind exception: {bindException}, errorCode: {bindException.SocketErrorCode}");
                return false;
            }
            return true;
        }
 
        internal async Task<int> SendRawAndRecycle(NetPacket packet, IPEndPoint remoteEndPoint, CancellationToken ct = default)
        {
            int result = await SendRaw(packet, remoteEndPoint, ct);
            PoolRecycle(packet);
            return result;
        }
        
        internal async Task<int> SendRaw(ArraySegment<byte> data, IPEndPoint remoteEndPoint, CancellationToken ct = default)
        {
            NetPacket? expandedPacket = null;
            if (_extraPacketLayer != null)
            {
                expandedPacket = PoolGetPacket(data.Count + _extraPacketLayer.ExtraPacketSizeForLayer);
                
                data.CopyTo(expandedPacket.RawData, 0);
                
                data = new ArraySegment<byte>(expandedPacket.RawData, 0, expandedPacket.Size);
                _extraPacketLayer.ProcessOutBoundPacket(ref remoteEndPoint, ref expandedPacket);
            }

            var socket = _udpSocketv4;
            if (remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 && IPv6Support)
            {
                socket = _udpSocketv6;
                if (socket == null)
                    return 0;
            }

            int result;
            try
            {
                result = await socket.SendToAsync(data, SocketFlags.None, remoteEndPoint);
                //NetDebug.WriteForce("[S]Send packet to {0}, result: {1}", remoteEndPoint, result);
            }
            catch (SocketException ex)
            {
                switch (ex.SocketErrorCode)
                {
                    case SocketError.NoBufferSpaceAvailable:
                    case SocketError.Interrupted:
                        return 0;
                    case SocketError.MessageSize:
                        NetDebug.Write(NetLogLevel.Trace, $"[SRD] 10040, datalen: {data.Count}");
                        return 0;

                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable:
                        if (DisconnectOnUnreachable && TryGetPeer(remoteEndPoint, out var fromPeer))
                        {
                            await ForceDisconnectPeerAsync(
                                fromPeer,
                                ex.SocketErrorCode == SocketError.HostUnreachable
                                    ? DisconnectReason.HostUnreachable
                                    : DisconnectReason.NetworkUnreachable,
                                ex.SocketErrorCode,
                                null);
                        }

                        if (OnNetworkError != null) await OnNetworkError(remoteEndPoint, ex.SocketErrorCode);
                        return -1;

                    case SocketError.Shutdown:
                        if (OnNetworkError != null) await OnNetworkError(remoteEndPoint, ex.SocketErrorCode);
                        return -1;

                    default:
                        NetDebug.WriteError($"[S] {ex}");
                        return -1;
                }
            }
            catch (Exception ex)
            {
                NetDebug.WriteError($"[S] {ex}");
                return 0;
            }
            finally
            {
                if (expandedPacket != null)
                {
                    PoolRecycle(expandedPacket);
                }
            }

            if (result <= 0)
                return 0;

            if (EnableStatistics)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(data.Count);
            }

            return result;
        }

        public async Task<bool> SendBroadcast(ArraySegment<byte> data, int port)
        {
            NetPacket packet;
            if (_extraPacketLayer != null)
            {
                var headerSize = NetPacket.GetHeaderSize(PacketProperty.Broadcast);
                packet = PoolGetPacket(headerSize + data.Count + _extraPacketLayer.ExtraPacketSizeForLayer);
                packet.Property = PacketProperty.Broadcast;
                Buffer.BlockCopy(data.Array, data.Offset, packet.RawData, headerSize, data.Count);
                var checksumComputeStart = 0;
                int preCrcLength = data.Count + headerSize;
                IPEndPoint emptyEp = null;
                _extraPacketLayer.ProcessOutBoundPacket(ref emptyEp, ref packet);
            }
            else
            {
                packet = PoolGetWithData(PacketProperty.Broadcast, data);
            }

            bool broadcastSuccess = false;
            bool multicastSuccess = false;
            try
            {
                broadcastSuccess = await _udpSocketv4.SendToAsync(
                    packet,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Broadcast, port)) > 0;

                if (_udpSocketv6 != null)
                {
                    multicastSuccess = await _udpSocketv6.SendToAsync(
                        packet,
                        SocketFlags.None,
                        new IPEndPoint(MulticastAddressV6, port)) > 0;
                }
            }
            catch (Exception ex)
            {
                NetDebug.WriteError($"[S][MCAST] {ex}");
                return broadcastSuccess;
            }
            finally
            {
                PoolRecycle(packet);
            }

            return broadcastSuccess || multicastSuccess;
        }
        public void CloseSocket()
        {
            _udpSocketv4?.Close();
            _udpSocketv6?.Close();
            _udpSocketv4?.Dispose();
            _udpSocketv6?.Dispose();
            _udpSocketv4 = null;
            _udpSocketv6 = null;
        }
    }
}
