using System;
using Netflat.Native;
using UnityEngine;

namespace Netflat
{
    public sealed class NetworkClient : IDisposable
    {
        private const string LogTag = "Client";

        private readonly INetflat _netflat;

        private readonly ILogger _logger;

        private readonly PacketCodec _codec;

        public NetworkClient(INetflat netflat, ILogger logger)
        {
            _netflat = netflat;
            _logger = logger;
            _codec = new PacketCodec(netflat.MaxPacketSize, logger, LogTag);
        }

        public event Action Connected;

        public event Action<DisconnectReason> Disconnected;

        public ClientState State { get; private set; }

        public bool IsConnected => State == ClientState.Connected;

        public string ServerAddress { get; private set; }

        public string DefaultEndpoint => _netflat.DefaultEndpoint;

        public bool Connect(string endpoint, out string error)
        {
            if (State != ClientState.Disconnected)
            {
                error = "The client is already connected.";
                return false;
            }

            if (!_netflat.ClientConnect(endpoint, out error))
            {
                _logger.LogError(LogTag, $"Failed to connect to '{endpoint}' via {_netflat.Name}: {error}");
                return false;
            }

            State = ClientState.Connecting;

            _logger.Log(LogTag, $"Connecting to '{endpoint}' via {_netflat.Name}.");
            return true;
        }

        public void Disconnect()
        {
            if (State == ClientState.Disconnected)
            {
                return;
            }

            _netflat.ClientDisconnect();

            Close(DisconnectReason.Requested);
        }

        public IDisposable On<T>(Action<T> handler) where T : struct, IPacket
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            return _codec.Subscribe<T>((_, packet) => handler(packet));
        }

        public bool Send<T>(T packet) where T : struct, IPacket
        {
            if (!IsConnected || !_codec.TryEncode(packet, out ReadOnlySpan<byte> data))
            {
                return false;
            }

            return _netflat.ClientSend(data, packet.Delivery.ToNetflat());
        }

        public void Tick()
        {
            while (State != ClientState.Disconnected && _netflat.ClientPoll(out NetflatClientEvent evt))
            {
                Handle(evt);
            }

            while (State == ClientState.Connected && _netflat.ClientReceive(out ReadOnlySpan<byte> data))
            {
                _codec.Decode(default, ServerAddress, data);
            }
        }

        public void Dispose()
        {
            if (State == ClientState.Disconnected)
            {
                return;
            }

            _netflat.ClientDisconnect();

            State = ClientState.Disconnected;
        }

        private void Handle(NetflatClientEvent evt)
        {
            switch (evt.Type)
            {
                case NetflatClientEventType.Ready:
                    State = ClientState.Connected;
                    ServerAddress = evt.Address ?? "server";
                    _logger.Log(LogTag, $"Connected to {ServerAddress}.");
                    Connected?.Invoke();
                    break;

                case NetflatClientEventType.Closed:
                    Close(State == ClientState.Connected ? DisconnectReason.Lost : DisconnectReason.Failed);
                    break;
            }
        }

        private void Close(DisconnectReason reason)
        {
            State = ClientState.Disconnected;
            ServerAddress = null;

            _codec.ResetWarnings();

            switch (reason)
            {
                case DisconnectReason.Requested:
                    _logger.Log(LogTag, "Disconnected.");
                    break;

                case DisconnectReason.Failed:
                    _logger.LogWarning(LogTag, "The server did not answer.");
                    break;

                case DisconnectReason.Lost:
                    _logger.LogWarning(LogTag, "Connection lost.");
                    break;
            }

            Disconnected?.Invoke(reason);
        }
    }

    public enum ClientState
    {
        Disconnected,

        Connecting,

        Connected
    }

    public enum DisconnectReason
    {
        Requested,

        Failed,

        Lost
    }
}
