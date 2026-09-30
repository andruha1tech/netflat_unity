using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Netflat.Native
{
    public sealed unsafe class NetflatSteam : INetflat
    {
        private static NetflatSteam s_instance;

        private readonly NetflatLoopback _loopback = new NetflatLoopback(NetflatAbi.MaxPacket);

        private readonly string _initError;

        private bool _initialized;

        private bool _disposed;

        private bool _serverRunning;

        private ulong _lobby;

        private bool _clientOpen;

        public NetflatSteam()
        {
            if (s_instance != null)
            {
                throw new InvalidOperationException($"{Dll.Library} keeps global native state, only one {nameof(NetflatSteam)} may exist.");
            }

            s_instance = this;

            _initialized = TryInitialize(out _initError);
        }

        public string Name => "steam";

        public int MaxPacketSize => NetflatAbi.MaxPacket;

        public string DefaultEndpoint => string.Empty;

        public string ServerLocalEndpoint => _lobby != 0 ? FormatId(_lobby) : null;

        public bool ServerStart(string endpoint, int maxPeers, out string error)
        {
            ThrowIfDisposed();

            if (_serverRunning)
            {
                error = "The server is already running.";
                return false;
            }

            if (!_initialized)
            {
                error = _initError;
                return false;
            }

            if (Dll.nf_serv_start($"peers={maxPeers}") != 0)
            {
                error = "Steam could not start creating a lobby.";
                return false;
            }

            _serverRunning = true;
            _lobby = 0;

            error = null;
            return true;
        }

        public void ServerStop()
        {
            if (!_serverRunning)
            {
                return;
            }

            ReleaseServer();
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

            if (type < 0)
            {
                ReleaseServer();
                evt = new NetflatServerEvent(NetflatServerEventType.Closed);
                return true;
            }

            evt = NetflatServerEvent.Decode(type, new ReadOnlySpan<byte>(data, (int)size));

            switch (evt.Type)
            {
                case NetflatServerEventType.Ready:
                    TryParseId(evt.Address, out _lobby);
                    break;

                case NetflatServerEventType.Closed:
                    ReleaseServer();
                    break;

                default:
                    evt = new NetflatServerEvent(evt.Type, evt.Peer, evt.Address ?? FormatId(evt.Peer));
                    break;
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

            if (!TryParseId(endpoint, out ulong lobby))
            {
                error = $"'{endpoint}' is not a Steam lobby id.";
                return false;
            }

            if (_serverRunning && lobby == _lobby)
            {
                _loopback.Connect(FormatId(lobby));

                error = null;
                return true;
            }

            if (!_initialized)
            {
                error = _initError;
                return false;
            }

            if (Dll.nf_clnt_connect($"address={FormatId(lobby)}") != 0)
            {
                error = "Steam could not start joining the lobby.";
                return false;
            }

            _clientOpen = true;

            error = null;
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
            _lobby = 0;

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

        private void ReleaseServer()
        {
            _serverRunning = false;
            _lobby = 0;

            _loopback.StopServer();

            Dll.nf_serv_stop();
        }

        private static bool TryInitialize(out string error)
        {
            error = null;

            try
            {
                Dll.nf_cmn_shutdown();

                if (Dll.nf_cmn_init(string.Empty) != 0)
                {
                    error = "SteamAPI_Init failed: Steam must be running and logged in, and steam_appid.txt must be in the working directory.";
                    return false;
                }
            }
            catch (DllNotFoundException)
            {
                error = $"Native library '{Dll.Library}' or its dependency steam_api64 was not found.";
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                error = $"'{Dll.Library}' does not match the netflat ABI: {exception.Message}";
                return false;
            }

            return true;
        }

        private static bool TryParseId(string text, out ulong id) =>
            ulong.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;

        private static string FormatId(ulong id) => id.ToString(CultureInfo.InvariantCulture);

        private static bool IsSendable(ReadOnlySpan<byte> data) => !data.IsEmpty && data.Length <= NetflatAbi.MaxPacket;

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NetflatSteam));
            }
        }

        private static class Dll
        {
            public const string Library = "netflat_steam";

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
