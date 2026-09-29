using System;
using System.Collections.Generic;
using Netflat.Native;
using UnityEngine;

namespace Netflat
{
    public sealed class NetworkServer : IDisposable
    {
        private const string LogTag = "Server";

        private readonly INetflat _netflat;

        private readonly ILogger _logger;

        private readonly PacketCodec _codec;

        private readonly HashSet<PeerId> _peers = new HashSet<PeerId>();

        private readonly Dictionary<PeerId, string> _addresses = new Dictionary<PeerId, string>();

        public NetworkServer(INetflat netflat, ILogger logger)
        {
            _netflat = netflat;
            _logger = logger;
            _codec = new PacketCodec(netflat.MaxPacketSize, logger, LogTag);
        }

        public event Action Started;

        public event Action Stopped;

        public event Action<PeerId> PeerConnected;

        public event Action<PeerId> PeerDisconnected;

        public ServerState State { get; private set; }

        public bool IsRunning => State == ServerState.Running;

        public int MaxPeers { get; set; } = 16;

        public IReadOnlyCollection<PeerId> Peers => _peers;

        public string LocalEndpoint => _netflat.ServerLocalEndpoint;

        public string Address { get; private set; }

        public bool Start(string endpoint, out string error)
        {
            if (State != ServerState.Stopped)
            {
                error = "The server is already running.";
                return false;
            }

            if (!_netflat.ServerStart(endpoint, out error))
            {
                _logger.LogError(LogTag, $"Failed to start on '{endpoint}' via {_netflat.Name}: {error}");
                return false;
            }

            State = ServerState.Starting;

            _logger.Log(LogTag, $"Starting on '{endpoint}' via {_netflat.Name}.");
            return true;
        }

        public void Stop()
        {
            if (State == ServerState.Stopped)
            {
                return;
            }

            _netflat.ServerStop();

            Reset();

            _logger.Log(LogTag, "Stopped.");

            Stopped?.Invoke();
        }

        public void Kick(PeerId peer)
        {
            if (!_peers.Contains(peer))
            {
                return;
            }

            _logger.Log(LogTag, $"Kicking {GetAddress(peer)}.");

            _netflat.ServerDrop(peer.Value);
        }

        public string GetAddress(PeerId peer) => _addresses.TryGetValue(peer, out string address) ? address : peer.ToString();

        public IDisposable On<T>(Action<PeerId, T> handler) where T : struct, IPacket => _codec.Subscribe(handler);

        public bool Send<T>(PeerId peer, T packet) where T : struct, IPacket
        {
            if (!IsRunning || !_peers.Contains(peer) || !_codec.TryEncode(packet, out ReadOnlySpan<byte> data))
            {
                return false;
            }

            return _netflat.ServerSend(peer.Value, data, packet.Delivery.ToNetflat());
        }

        public void SendToAll<T>(T packet) where T : struct, IPacket => Broadcast(packet, null);

        public void SendToAllExcept<T>(T packet, PeerId excluded) where T : struct, IPacket => Broadcast(packet, excluded);

        public void Tick()
        {
            while (State != ServerState.Stopped && _netflat.ServerPoll(out NetflatServerEvent evt))
            {
                Handle(evt);
            }

            while (State == ServerState.Running && _netflat.ServerReceive(out ulong from, out ReadOnlySpan<byte> data))
            {
                var peer = new PeerId(from);

                if (_peers.Contains(peer))
                {
                    _codec.Decode(peer, GetAddress(peer), data);
                }
            }
        }

        public void Dispose()
        {
            if (State == ServerState.Stopped)
            {
                return;
            }

            _netflat.ServerStop();

            Reset();
        }

        private void Broadcast<T>(T packet, PeerId? excluded) where T : struct, IPacket
        {
            if (!IsRunning || _peers.Count == 0 || !_codec.TryEncode(packet, out ReadOnlySpan<byte> data))
            {
                return;
            }

            NetflatDelivery delivery = packet.Delivery.ToNetflat();

            foreach (PeerId peer in _peers)
            {
                if (peer != excluded)
                {
                    _netflat.ServerSend(peer.Value, data, delivery);
                }
            }
        }

        private void Handle(NetflatServerEvent evt)
        {
            var peer = new PeerId(evt.Peer);

            switch (evt.Type)
            {
                case NetflatServerEventType.Ready:
                    State = ServerState.Running;
                    Address = evt.Address;
                    _logger.Log(LogTag, $"Listening on {evt.Address}.");
                    Started?.Invoke();
                    break;

                case NetflatServerEventType.Closed:
                    Reset();
                    _logger.LogWarning(LogTag, "Closed by the transport.");
                    Stopped?.Invoke();
                    break;

                case NetflatServerEventType.PeerRequest:
                    Admit(peer, evt.Address);
                    break;

                case NetflatServerEventType.PeerJoined:
                    _peers.Add(peer);
                    _logger.Log(LogTag, $"{GetAddress(peer)} connected ({_peers.Count}/{MaxPeers}).");
                    PeerConnected?.Invoke(peer);
                    break;

                case NetflatServerEventType.PeerLeft:
                    if (_peers.Remove(peer))
                    {
                        _logger.Log(LogTag, $"{GetAddress(peer)} disconnected ({_peers.Count}/{MaxPeers}).");
                        PeerDisconnected?.Invoke(peer);
                    }

                    _addresses.Remove(peer);
                    break;
            }
        }

        private void Admit(PeerId peer, string address)
        {
            _addresses[peer] = address ?? peer.ToString();

            if (_peers.Count >= MaxPeers)
            {
                _logger.LogWarning(LogTag, $"Rejected {GetAddress(peer)}: the server is full.");
                _netflat.ServerReject(peer.Value);
                return;
            }

            _netflat.ServerAccept(peer.Value);
        }

        private void Reset()
        {
            State = ServerState.Stopped;
            Address = null;

            _peers.Clear();
            _addresses.Clear();
            _codec.ResetWarnings();
        }
    }

    public enum ServerState
    {
        Stopped,

        Starting,

        Running
    }
}
