using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Netflat.Native;
using UnityEngine;

namespace Netflat
{
    public readonly struct PeerId : IEquatable<PeerId>
    {
        public PeerId(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        public bool Equals(PeerId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is PeerId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => $"#{Value:X}";

        public static bool operator ==(PeerId left, PeerId right) => left.Equals(right);

        public static bool operator !=(PeerId left, PeerId right) => !left.Equals(right);
    }

    public interface IPacket
    {
        PacketDelivery Delivery { get; }

        void Write(ref PacketWriter writer);

        void Read(ref PacketReader reader);
    }

    public enum PacketDelivery
    {
        Reliable,

        Unreliable
    }

    public static class PacketId
    {
        private const uint OffsetBasis = 2166136261;

        private const uint Prime = 16777619;

        public static uint Of(Type type)
        {
            uint hash = OffsetBasis;

            foreach (char symbol in type.FullName)
            {
                hash ^= symbol;
                hash *= Prime;
            }

            return hash;
        }
    }

    public static class PacketId<T> where T : struct, IPacket
    {
        public static readonly uint Value = PacketId.Of(typeof(T));
    }

    public sealed class PacketException : Exception
    {
        public PacketException(string message) : base(message)
        {
        }
    }

    public ref struct PacketWriter
    {
        private readonly Span<byte> _buffer;

        private int _length;

        public PacketWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _length = 0;
        }

        public int Length => _length;

        public ReadOnlySpan<byte> Written => _buffer.Slice(0, _length);

        public void WriteByte(byte value) => Take(sizeof(byte))[0] = value;

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUShort(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(sizeof(ushort)), value);

        public void WriteInt(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(sizeof(int)), value);

        public void WriteUInt(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(sizeof(uint)), value);

        public void WriteFloat(float value) => WriteInt(BitConverter.SingleToInt32Bits(value));

        public void WriteVector2(Vector2 value)
        {
            WriteFloat(value.x);
            WriteFloat(value.y);
        }

        public void WriteVector3(Vector3 value)
        {
            WriteFloat(value.x);
            WriteFloat(value.y);
            WriteFloat(value.z);
        }

        public void WriteString(string value)
        {
            value ??= string.Empty;

            int size = Encoding.UTF8.GetByteCount(value);

            if (size > ushort.MaxValue)
            {
                throw new PacketException($"String of {size} bytes exceeds the {ushort.MaxValue} byte limit.");
            }

            WriteUShort((ushort)size);

            Encoding.UTF8.GetBytes(value, Take(size));
        }

        private Span<byte> Take(int size)
        {
            if (_length + size > _buffer.Length)
            {
                throw new PacketException($"Packet does not fit into {_buffer.Length} bytes.");
            }

            Span<byte> slice = _buffer.Slice(_length, size);

            _length += size;

            return slice;
        }
    }

    public ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _buffer;

        private int _position;

        public PacketReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        public int Remaining => _buffer.Length - _position;

        public byte ReadByte() => Take(sizeof(byte))[0];

        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));

        public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

        public uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));

        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());

        public Vector2 ReadVector2() => new Vector2(ReadFloat(), ReadFloat());

        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());

        public string ReadString() => Encoding.UTF8.GetString(Take(ReadUShort()));

        private ReadOnlySpan<byte> Take(int size)
        {
            if (size > Remaining)
            {
                throw new PacketException($"Packet is truncated: {size} bytes expected at offset {_position}, {Remaining} left.");
            }

            ReadOnlySpan<byte> slice = _buffer.Slice(_position, size);

            _position += size;

            return slice;
        }
    }

    internal sealed class PacketCodec
    {
        private readonly PacketHandlers _handlers = new PacketHandlers();

        private readonly HashSet<uint> _reportedUnhandled = new HashSet<uint>();

        private readonly byte[] _buffer;

        private readonly ILogger _logger;

        private readonly string _logTag;

        public PacketCodec(int maxPacketSize, ILogger logger, string logTag)
        {
            _buffer = new byte[maxPacketSize];
            _logger = logger;
            _logTag = logTag;
        }

        public IDisposable Subscribe<T>(Action<PeerId, T> handler) where T : struct, IPacket => _handlers.Add(handler);

        public bool TryEncode<T>(T packet, out ReadOnlySpan<byte> data) where T : struct, IPacket
        {
            var writer = new PacketWriter(_buffer);

            try
            {
                writer.WriteUInt(PacketId<T>.Value);

                packet.Write(ref writer);
            }
            catch (PacketException exception)
            {
                _logger.LogError(_logTag, $"Cannot send {typeof(T).Name}: {exception.Message}");

                data = default;
                return false;
            }

            data = new ReadOnlySpan<byte>(_buffer, 0, writer.Length);
            return true;
        }

        public void Decode(PeerId sender, string senderName, ReadOnlySpan<byte> data)
        {
            var reader = new PacketReader(data);

            try
            {
                uint id = reader.ReadUInt();

                if (!_handlers.Dispatch(id, sender, ref reader) && _reportedUnhandled.Add(id))
                {
                    _logger.Log(_logTag, $"Ignoring packet 0x{id:X8} from {senderName}: nothing handles it yet (reported once).");
                }
            }
            catch (PacketException exception)
            {
                _logger.LogWarning(_logTag, $"Malformed packet from {senderName}: {exception.Message}");
            }
            catch (Exception exception)
            {
                _logger.LogException(exception);
            }
        }

        public void ResetWarnings() => _reportedUnhandled.Clear();
    }

    internal sealed class PacketHandlers
    {
        private readonly Dictionary<uint, Route> _routes = new Dictionary<uint, Route>();

        public IDisposable Add<T>(Action<PeerId, T> handler) where T : struct, IPacket
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            uint id = PacketId<T>.Value;

            if (!_routes.TryGetValue(id, out Route route))
            {
                route = new Route<T>();

                _routes.Add(id, route);
            }
            else if (route.PacketType != typeof(T))
            {
                throw new InvalidOperationException($"{typeof(T)} and {route.PacketType} share packet id 0x{id:X8}; rename one of them.");
            }

            var typed = (Route<T>)route;

            typed.Handler += handler;

            return new Subscription(() => typed.Handler -= handler);
        }

        public bool Dispatch(uint id, PeerId sender, ref PacketReader reader)
        {
            if (!_routes.TryGetValue(id, out Route route) || !route.HasHandlers)
            {
                return false;
            }

            route.Dispatch(sender, ref reader);

            return true;
        }

        private abstract class Route
        {
            public abstract Type PacketType { get; }

            public abstract bool HasHandlers { get; }

            public abstract void Dispatch(PeerId sender, ref PacketReader reader);
        }

        private sealed class Route<T> : Route where T : struct, IPacket
        {
            public Action<PeerId, T> Handler;

            public override Type PacketType => typeof(T);

            public override bool HasHandlers => Handler != null;

            public override void Dispatch(PeerId sender, ref PacketReader reader)
            {
                T packet = default;

                packet.Read(ref reader);

                Handler?.Invoke(sender, packet);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Action _unsubscribe;

            public Subscription(Action unsubscribe)
            {
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                _unsubscribe?.Invoke();
                _unsubscribe = null;
            }
        }
    }

    internal static class PacketDeliveryExtensions
    {
        public static NetflatDelivery ToNetflat(this PacketDelivery delivery) =>
            delivery == PacketDelivery.Reliable ? NetflatDelivery.Reliable : NetflatDelivery.Unreliable;
    }
}
