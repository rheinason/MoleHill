using System.Buffers.Binary;

namespace MoleHill.Core.Engine;

/// <summary>
/// Incremental, deterministic XXH64 implementation for cache fingerprints.
/// </summary>
public sealed class XxHash64Builder
{
    private const int StripeLength = 32;
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    private readonly ulong _seed;
    private readonly byte[] _buffer = new byte[StripeLength];
    private ulong _v1;
    private ulong _v2;
    private ulong _v3;
    private ulong _v4;
    private ulong _totalLength;
    private int _bufferSize;

    public XxHash64Builder(ulong seed = 0)
    {
        _seed = seed;
        _v1 = seed + Prime1 + Prime2;
        _v2 = seed + Prime2;
        _v3 = seed;
        _v4 = seed - Prime1;
    }

    public static ulong ComputeHash(ReadOnlySpan<byte> bytes, ulong seed = 0)
    {
        var builder = new XxHash64Builder(seed);
        builder.AddBytes(bytes);
        return builder.ToUInt64();
    }

    public void Add(bool value) => Add(value ? 1 : 0);

    public void Add(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        AddBytes(buffer);
    }

    public void Add(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        AddBytes(buffer);
    }

    public void Add(uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        AddBytes(buffer);
    }

    public void Add(long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        AddBytes(buffer);
    }

    public void Add(ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        AddBytes(buffer);
    }

    public void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

    public void Add(Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        value.TryWriteBytes(buffer);
        AddBytes(buffer);
    }

    public void AddBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        _totalLength += (ulong)bytes.Length;
        int offset = 0;

        if (_bufferSize + bytes.Length < StripeLength)
        {
            bytes.CopyTo(_buffer.AsSpan(_bufferSize));
            _bufferSize += bytes.Length;
            return;
        }

        if (_bufferSize > 0)
        {
            int fill = StripeLength - _bufferSize;
            bytes[..fill].CopyTo(_buffer.AsSpan(_bufferSize));
            ProcessStripe(_buffer);
            offset += fill;
            _bufferSize = 0;
        }

        while (offset <= bytes.Length - StripeLength)
        {
            ProcessStripe(bytes.Slice(offset, StripeLength));
            offset += StripeLength;
        }

        if (offset < bytes.Length)
        {
            bytes[offset..].CopyTo(_buffer);
            _bufferSize = bytes.Length - offset;
        }
    }

    public ulong ToUInt64()
    {
        ulong hash;
        if (_totalLength >= StripeLength)
        {
            hash =
                RotateLeft(_v1, 1) +
                RotateLeft(_v2, 7) +
                RotateLeft(_v3, 12) +
                RotateLeft(_v4, 18);
            hash = MergeAccumulator(hash, _v1);
            hash = MergeAccumulator(hash, _v2);
            hash = MergeAccumulator(hash, _v3);
            hash = MergeAccumulator(hash, _v4);
        }
        else
        {
            hash = _seed + Prime5;
        }

        hash += _totalLength;
        return Avalanche(ProcessTail(hash, _buffer.AsSpan(0, _bufferSize)));
    }

    private void ProcessStripe(ReadOnlySpan<byte> stripe)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(stripe));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(stripe[8..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(stripe[16..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(stripe[24..]));
    }

    private static ulong ProcessTail(ulong hash, ReadOnlySpan<byte> tail)
    {
        int offset = 0;
        while (offset <= tail.Length - sizeof(ulong))
        {
            ulong lane = BinaryPrimitives.ReadUInt64LittleEndian(tail[offset..]);
            hash ^= Round(0, lane);
            hash = (RotateLeft(hash, 27) * Prime1) + Prime4;
            offset += sizeof(ulong);
        }

        if (offset <= tail.Length - sizeof(uint))
        {
            hash ^= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(tail[offset..]) * Prime1;
            hash = (RotateLeft(hash, 23) * Prime2) + Prime3;
            offset += sizeof(uint);
        }

        while (offset < tail.Length)
        {
            hash ^= tail[offset] * Prime5;
            hash = RotateLeft(hash, 11) * Prime1;
            offset++;
        }

        return hash;
    }

    private static ulong Round(ulong accumulator, ulong lane) =>
        RotateLeft(accumulator + (lane * Prime2), 31) * Prime1;

    private static ulong MergeAccumulator(ulong hash, ulong accumulator)
    {
        hash ^= Round(0, accumulator);
        return (hash * Prime1) + Prime4;
    }

    private static ulong Avalanche(ulong hash)
    {
        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        hash ^= hash >> 32;
        return hash;
    }

    private static ulong RotateLeft(ulong value, int offset) =>
        (value << offset) | (value >> (64 - offset));
}
