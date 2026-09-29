using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Netflat.Native
{
    public sealed unsafe class NetflatRudp : INetflat
    {
        public const ushort DefaultPort = 7777;

        private const string LocalHost = "127.0.0.1";

        private static NetflatRudp s_instance;

        private readonly NetflatLoopback _loopback = new NetflatLoopback(NetflatAbi.MaxPacket);

        private bool _initialized;

        private bool _disposed;

        private bool _serverRunning;

        private ushort _serverPort;

        private bool _clientOpen;

        public NetflatRudp()
        {
            if (s_instance != null)
            {
                throw new InvalidOperationException($"{Dll.Library} keeps global native state, only one {nameof(NetflatRudp)} may exist.");
            }

            s_instance = this;
        }

        public string Name => "rudp";

        public int MaxPacketSize => NetflatAbi.MaxPacket;

        public string DefaultEndpoint => $"{LocalHost}:{DefaultPort}";

        public string ServerLocalEndpoint => _serverRunning ? $"{LocalHost}:{_serverPort}" : null;

        public bool ServerStart(string endpoint, out string error)
        {
            ThrowIfDisposed();

            if (_serverRunning)
            {
                error = "The server is already running.";
                return false;
            }

            if (!TryParsePort(endpoint, out ushort port))
            {
                error = $"'{endpoint}' has no valid port, expected [host:]port with a port from 1 to 65535.";
                return false;
            }

            if (!EnsureInitialized(out error))
            {
                return false;
            }

            if (Dll.nf_serv_start($"port={port}") != 0)
            {
                error = $"UDP port {port} is unavailable.";
                return false;
            }

            _serverRunning = true;
            _serverPort = port;
            return true;
        }

        public void ServerStop()
        {
            if (!_serverRunning)
            {
                return;
            }

            _serverRunning = false;
            _loopback.StopServer();

            Dll.nf_serv_stop();
        }

        public bool ServerPoll(out NetflatServerEvent evt)
        {
            if (!_serverRunning)
            {
                evt = default;
                return false;
            }

            if (_loopback.TryPollServer(out evt))
            {
                return true;
            }

            int type = Dll.nf_serv_service(out byte* data, out uint size);

            if (type == NetflatAbi.EventNone)
            {
                return false;
            }

            evt = type < 0
                ? new NetflatServerEvent(NetflatServerEventType.Closed)
                : NetflatServerEvent.Decode(type, new ReadOnlySpan<byte>(data, (int)size));

            if (evt.Type == NetflatServerEventType.Closed)
            {
                _serverRunning = false;
                _loopback.StopServer();
            }

            return true;
        }

        public bool ServerReceive(out ulong peer, out ReadOnlySpan<byte> data)
        {
            peer = 0;
            data = default;

            if (!_serverRunning)
            {
                return false;
            }

            if (_loopback.TryReceiveOnServer(out data))
            {
                peer = NetflatLoopback.Peer;
                return true;
            }

            byte* pointer = Dll.nf_serv_receive(out uint size, out peer);

            if (pointer == null)
            {
                return false;
            }

            data = new ReadOnlySpan<byte>(pointer, (int)size);
            return true;
        }

        public bool ServerSend(ulong peer, ReadOnlySpan<byte> data, NetflatDelivery delivery)
        {
            if (!_serverRunning || !IsSendable(data))
            {
                return false;
            }

            if (peer == NetflatLoopback.Peer)
            {
                return _loopback.SendToClient(data);
            }

            fixed (byte* pointer = data)
            {
                int result = delivery == NetflatDelivery.Reliable
                    ? Dll.nf_serv_send_reliable(peer, pointer, (uint)data.Length)
                    : Dll.nf_serv_send_unreliable(peer, pointer, (uint)data.Length);

                return result == 0;
            }
        }

        public void ServerAccept(ulong peer)
        {
            if (!_serverRunning)
            {
                return;
            }

            if (peer == NetflatLoopback.Peer)
            {
                _loopback.Accept();
                return;
            }

            Dll.nf_serv_accept(peer);
        }

        public void ServerReject(ulong peer)
        {
            if (!_serverRunning)
            {
                return;
            }

            if (peer == NetflatLoopback.Peer)
            {
                _loopback.DropByServer();
                return;
            }

            Dll.nf_serv_reject(peer);
        }

        public void ServerDrop(ulong peer)
        {
            if (!_serverRunning)
            {
                return;
            }

            if (peer == NetflatLoopback.Peer)
            {
                _loopback.DropByServer();
                return;
            }

            Dll.nf_serv_drop(peer);
        }

        public bool ClientConnect(string endpoint, out string error)
        {
            ThrowIfDisposed();

            if (_clientOpen || _loopback.IsOpen)
            {
                error = "The client is already connected.";
                return false;
            }

            endpoint = endpoint?.Trim();

            if (_serverRunning && endpoint == ServerLocalEndpoint)
            {
                _loopback.Connect(endpoint);

                error = null;
                return true;
            }

            if (!EnsureInitialized(out error))
            {
                return false;
            }

            if (Dll.nf_clnt_connect($"address={endpoint}") != 0)
            {
                error = $"Could not connect to '{endpoint}', expected a.b.c.d:port.";
                return false;
            }

            _clientOpen = true;
            return true;
        }

        public void ClientDisconnect()
        {
            _loopback.DisconnectClient();

            if (!_clientOpen)
            {
                return;
            }

            _clientOpen = false;

            Dll.nf_clnt_disconnect();
        }

        public bool ClientPoll(out NetflatClientEvent evt)
        {
            if (_loopback.TryPollClient(out evt))
            {
                return true;
            }

            if (!_clientOpen)
            {
                return false;
            }

            int type = Dll.nf_clnt_service(out byte* data, out uint size);

            if (type == NetflatAbi.EventNone)
            {
                return false;
            }

            evt = type < 0
                ? new NetflatClientEvent(NetflatClientEventType.Closed)
                : NetflatClientEvent.Decode(type, new ReadOnlySpan<byte>(data, (int)size));

            if (evt.Type == NetflatClientEventType.Closed)
            {
                _clientOpen = false;

                Dll.nf_clnt_disconnect();
            }

            return true;
        }

        public bool ClientReceive(out ReadOnlySpan<byte> data)
        {
            if (_loopback.TryReceiveOnClient(out data))
            {
                return true;
            }

            if (!_clientOpen)
            {
                return false;
            }

            byte* pointer = Dll.nf_clnt_receive(out uint size);

            if (pointer == null)
            {
                return false;
            }

            data = new ReadOnlySpan<byte>(pointer, (int)size);
            return true;
        }

        public bool ClientSend(ReadOnlySpan<byte> data, NetflatDelivery delivery)
        {
            if (!IsSendable(data))
            {
                return false;
            }

            if (_loopback.IsJoined)
            {
                return _loopback.SendToServer(data);
            }

            if (!_clientOpen)
            {
                return false;
            }

            fixed (byte* pointer = data)
            {
                int result = delivery == NetflatDelivery.Reliable
                    ? Dll.nf_clnt_send_reliable(pointer, (uint)data.Length)
                    : Dll.nf_clnt_send_unreliable(pointer, (uint)data.Length);

                return result == 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _loopback.DisconnectClient();
            _loopback.StopServer();

            _serverRunning = false;
            _clientOpen = false;

            if (_initialized)
            {
                _initialized = false;

                Dll.nf_cmn_shutdown();
            }

            if (s_instance == this)
            {
                s_instance = null;
            }
        }

        private bool EnsureInitialized(out string error)
        {
            error = null;

            if (_initialized)
            {
                return true;
            }

            try
            {
                Dll.nf_cmn_shutdown();

                if (Dll.nf_cmn_init(string.Empty) != 0)
                {
                    error = $"{Dll.Library} failed to initialize.";
                    return false;
                }
            }
            catch (DllNotFoundException)
            {
                error = $"Native library '{Dll.Library}' was not found.";
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                error = $"'{Dll.Library}' does not match the netflat ABI: {exception.Message}";
                return false;
            }

            _initialized = true;
            return true;
        }

        private static bool TryParsePort(string endpoint, out ushort port)
        {
            port = DefaultPort;

            endpoint = endpoint?.Trim();

            if (string.IsNullOrEmpty(endpoint))
            {
                return true;
            }

            string text = endpoint.Substring(endpoint.LastIndexOf(':') + 1);

            return ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port != 0;
        }

        private static bool IsSendable(ReadOnlySpan<byte> data) => !data.IsEmpty && data.Length <= NetflatAbi.MaxPacket;

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NetflatRudp));
            }
        }

        private static class Dll
        {
            public const string Library = "netflat_rudp";

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_cmn_init([MarshalAs(UnmanagedType.LPStr)] string args);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void nf_cmn_shutdown();

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_serv_start([MarshalAs(UnmanagedType.LPStr)] string args);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void nf_serv_stop();

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_serv_service(out byte* outData, out uint outSize);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern byte* nf_serv_receive(out uint outSize, out ulong outFrom);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_serv_send_reliable(ulong peerId, byte* data, uint size);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_serv_send_unreliable(ulong peerId, byte* data, uint size);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void nf_serv_accept(ulong peerId);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void nf_serv_reject(ulong peerId);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void nf_serv_drop(ulong peerId);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_clnt_connect([MarshalAs(UnmanagedType.LPStr)] string args);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void nf_clnt_disconnect();

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_clnt_service(out byte* outData, out uint outSize);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern byte* nf_clnt_receive(out uint outSize);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_clnt_send_reliable(byte* data, uint size);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int nf_clnt_send_unreliable(byte* data, uint size);
        }
    }
}
