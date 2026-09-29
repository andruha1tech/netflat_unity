using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Netflat.Native
{
    public interface INetflat : IDisposable
    {
        string Name { get; }

        int MaxPacketSize { get; }

        string DefaultEndpoint { get; }

        string ServerLocalEndpoint { get; }

        bool ServerStart(string endpoint, out string error);

        void ServerStop();

        bool ServerPoll(out NetflatServerEvent evt);

        bool ServerReceive(out ulong peer, out ReadOnlySpan<byte> data);

        bool ServerSend(ulong peer, ReadOnlySpan<byte> data, NetflatDelivery delivery);

        void ServerAccept(ulong peer);

        void ServerReject(ulong peer);

        void ServerDrop(ulong peer);

        bool ClientConnect(string endpoint, out string error);

        void ClientDisconnect();

        bool ClientPoll(out NetflatClientEvent evt);

        bool ClientReceive(out ReadOnlySpan<byte> data);

        bool ClientSend(ReadOnlySpan<byte> data, NetflatDelivery delivery);
    }

    public enum NetflatDelivery
    {
        Reliable,

        Unreliable
    }

    public enum NetflatServerEventType
    {
        Ready = 1,

        Closed = 2,

        PeerRequest = 3,

        PeerJoined = 4,

        PeerLeft = 5
    }

    public readonly struct NetflatServerEvent
    {
        public NetflatServerEvent(NetflatServerEventType type, ulong peer = 0, string address = null)
        {
            Type = type;
            Peer = peer;
            Address = address;
        }

        public NetflatServerEventType Type { get; }

        public ulong Peer { get; }

        public string Address { get; }

        internal static NetflatServerEvent Decode(int type, ReadOnlySpan<byte> payload)
        {
            ulong peer = 0;
            string address = null;

            var reader = new NetflatPayloadReader(payload);

            while (reader.TryRead(out NetflatField field))
            {
                if (field.Kind == NetflatAbi.FieldU64 && peer == 0)
                {
                    peer = field.AsU64();
                }
                else if (field.Kind == NetflatAbi.FieldString && address == null)
                {
                    address = field.AsString();
                }
            }

            return new NetflatServerEvent((NetflatServerEventType)type, peer, address);
        }
    }

    public enum NetflatClientEventType
    {
        Ready = 1,

        Closed = 2
    }

    public readonly struct NetflatClientEvent
    {
        public NetflatClientEvent(NetflatClientEventType type, string address = null)
        {
            Type = type;
            Address = address;
        }

        public NetflatClientEventType Type { get; }

        public string Address { get; }

        internal static NetflatClientEvent Decode(int type, ReadOnlySpan<byte> payload)
        {
            string address = null;

            var reader = new NetflatPayloadReader(payload);

            while (reader.TryRead(out NetflatField field))
            {
                if (field.Kind == NetflatAbi.FieldString)
                {
                    address = field.AsString();
                    break;
                }
            }

            return new NetflatClientEvent((NetflatClientEventType)type, address);
        }
    }

    internal static class NetflatAbi
    {
        public const int MaxPacket = 1200;

        public const int EventNone = 0;

        public const byte FieldI32 = 1;

        public const byte FieldU64 = 2;

        public const byte FieldF32 = 3;

        public const byte FieldString = 4;

        public const byte FieldBlob = 5;
    }

    internal ref struct NetflatPayloadReader
    {
        private ReadOnlySpan<byte> _remaining;

        public NetflatPayloadReader(ReadOnlySpan<byte> payload)
        {
            _remaining = payload;
        }

        public bool TryRead(out NetflatField field)
        {
            field = default;

            if (_remaining.IsEmpty)
            {
                return false;
            }

            byte kind = _remaining[0];

            ReadOnlySpan<byte> body = _remaining.Slice(1);

            int size;

            switch (kind)
            {
                case NetflatAbi.FieldI32:
                case NetflatAbi.FieldF32:
                    size = sizeof(int);
                    break;

                case NetflatAbi.FieldU64:
                    size = sizeof(ulong);
                    break;

                case NetflatAbi.FieldString:
                case NetflatAbi.FieldBlob:
                    if (body.Length < sizeof(uint))
                    {
                        return false;
                    }

                    size = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
                    body = body.Slice(sizeof(uint));
                    break;

                default:
                    return false;
            }

            if (size < 0 || body.Length < size)
            {
                return false;
            }

            field = new NetflatField(kind, body.Slice(0, size));

            _remaining = body.Slice(size);

            return true;
        }
    }

    internal readonly ref struct NetflatField
    {
        public NetflatField(byte kind, ReadOnlySpan<byte> data)
        {
            Kind = kind;
            Data = data;
        }

        public byte Kind { get; }

        public ReadOnlySpan<byte> Data { get; }

        public ulong AsU64() => BinaryPrimitives.ReadUInt64LittleEndian(Data);

        public string AsString() => Encoding.UTF8.GetString(Data);
    }

    internal sealed class NetflatLoopback
    {
        public const ulong Peer = ulong.MaxValue;

        public const string PeerAddress = "local";

        private readonly int _maxPacketSize;

        private readonly Queue<NetflatServerEvent> _serverEvents = new Queue<NetflatServerEvent>();

        private readonly Queue<NetflatClientEvent> _clientEvents = new Queue<NetflatClientEvent>();

        private readonly Queue<Packet> _toServer = new Queue<Packet>();

        private readonly Queue<Packet> _toClient = new Queue<Packet>();

        private readonly Stack<byte[]> _pool = new Stack<byte[]>();

        private byte[] _heldByServer;

        private byte[] _heldByClient;

        private string _serverAddress;

        private LinkState _state;

        public NetflatLoopback(int maxPacketSize)
        {
            _maxPacketSize = maxPacketSize;
        }

        public bool IsOpen => _state != LinkState.Closed;

        public bool IsJoined => _state == LinkState.Joined;

        public void Connect(string serverAddress)
        {
            _serverAddress = serverAddress;
            _state = LinkState.Requested;

            _serverEvents.Enqueue(new NetflatServerEvent(NetflatServerEventType.PeerRequest, Peer, PeerAddress));
        }

        public void Accept()
        {
            if (_state != LinkState.Requested)
            {
                return;
            }

            _state = LinkState.Joined;

            _serverEvents.Enqueue(new NetflatServerEvent(NetflatServerEventType.PeerJoined, Peer, PeerAddress));
            _clientEvents.Enqueue(new NetflatClientEvent(NetflatClientEventType.Ready, _serverAddress));
        }

        public void DropByServer()
        {
            if (!IsOpen)
            {
                return;
            }

            Close();

            _serverEvents.Enqueue(new NetflatServerEvent(NetflatServerEventType.PeerLeft, Peer, PeerAddress));
            _clientEvents.Enqueue(new NetflatClientEvent(NetflatClientEventType.Closed));
        }

        public void StopServer()
        {
            _serverEvents.Clear();

            if (!IsOpen)
            {
                return;
            }

            Close();

            _clientEvents.Enqueue(new NetflatClientEvent(NetflatClientEventType.Closed));
        }

        public void DisconnectClient()
        {
            _clientEvents.Clear();

            if (!IsOpen)
            {
                return;
            }

            Close();

            _serverEvents.Enqueue(new NetflatServerEvent(NetflatServerEventType.PeerLeft, Peer, PeerAddress));
        }

        public bool TryPollServer(out NetflatServerEvent evt) => _serverEvents.TryDequeue(out evt);

        public bool TryPollClient(out NetflatClientEvent evt) => _clientEvents.TryDequeue(out evt);

        public bool SendToServer(ReadOnlySpan<byte> data) => IsJoined && Enqueue(_toServer, data);

        public bool SendToClient(ReadOnlySpan<byte> data) => IsJoined && Enqueue(_toClient, data);

        public bool TryReceiveOnServer(out ReadOnlySpan<byte> data) => TryReceive(_toServer, ref _heldByServer, out data);

        public bool TryReceiveOnClient(out ReadOnlySpan<byte> data) => TryReceive(_toClient, ref _heldByClient, out data);

        private bool Enqueue(Queue<Packet> queue, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty || data.Length > _maxPacketSize)
            {
                return false;
            }

            byte[] buffer = _pool.Count > 0 ? _pool.Pop() : new byte[_maxPacketSize];

            data.CopyTo(buffer);

            queue.Enqueue(new Packet(buffer, data.Length));

            return true;
        }

        private bool TryReceive(Queue<Packet> queue, ref byte[] held, out ReadOnlySpan<byte> data)
        {
            if (held != null)
            {
                _pool.Push(held);
                held = null;
            }

            if (!queue.TryDequeue(out Packet packet))
            {
                data = default;
                return false;
            }

            held = packet.Buffer;
            data = new ReadOnlySpan<byte>(packet.Buffer, 0, packet.Length);
            return true;
        }

        private void Close()
        {
            _state = LinkState.Closed;

            Recycle(_toServer);
            Recycle(_toClient);
        }

        private void Recycle(Queue<Packet> queue)
        {
            while (queue.TryDequeue(out Packet packet))
            {
                _pool.Push(packet.Buffer);
            }
        }

        private enum LinkState
        {
            Closed,

            Requested,

            Joined
        }

        private readonly struct Packet
        {
            public Packet(byte[] buffer, int length)
            {
                Buffer = buffer;
                Length = length;
            }

            public byte[] Buffer { get; }

            public int Length { get; }
        }
    }
}
