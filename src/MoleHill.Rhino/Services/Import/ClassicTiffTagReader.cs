using System.Buffers.Binary;

namespace MoleHill.Rhino.Services;

/// <summary>Reads primitive values from the first IFD of a classic TIFF, including unregistered GeoTIFF/GDAL tags.</summary>
internal static class ClassicTiffTagReader
{
    public static string? ReadAscii(string path, ushort tag)
    {
        if (!TryReadValue(path, tag, expectedType: 2, out byte[] bytes, out _))
            return null;
        return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n');
    }

    public static ushort[] ReadUnsignedShorts(string path, ushort tag)
    {
        if (!TryReadValue(path, tag, expectedType: 3, out byte[] bytes, out bool littleEndian))
            return Array.Empty<ushort>();
        var result = new ushort[bytes.Length / sizeof(ushort)];
        for (int index = 0; index < result.Length; index++)
        {
            ReadOnlySpan<byte> value = bytes.AsSpan(index * sizeof(ushort), sizeof(ushort));
            result[index] = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(value)
                : BinaryPrimitives.ReadUInt16BigEndian(value);
        }
        return result;
    }

    public static double[] ReadDoubles(string path, ushort tag)
    {
        if (!TryReadValue(path, tag, expectedType: 12, out byte[] bytes, out bool littleEndian))
            return Array.Empty<double>();
        var result = new double[bytes.Length / sizeof(double)];
        for (int index = 0; index < result.Length; index++)
        {
            ReadOnlySpan<byte> value = bytes.AsSpan(index * sizeof(double), sizeof(double));
            long bits = littleEndian
                ? BinaryPrimitives.ReadInt64LittleEndian(value)
                : BinaryPrimitives.ReadInt64BigEndian(value);
            result[index] = BitConverter.Int64BitsToDouble(bits);
        }
        return result.All(double.IsFinite) ? result : Array.Empty<double>();
    }

    private static bool TryReadValue(
        string path,
        ushort requestedTag,
        ushort expectedType,
        out byte[] value,
        out bool littleEndian)
    {
        value = Array.Empty<byte>();
        littleEndian = true;
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != header.Length)
            return false;
        littleEndian = header[0] == (byte)'I' && header[1] == (byte)'I';
        bool bigEndian = header[0] == (byte)'M' && header[1] == (byte)'M';
        if (!littleEndian && !bigEndian || ReadUInt16(header[2..4], littleEndian) != 42)
            return false;

        uint ifdOffset = ReadUInt32(header[4..8], littleEndian);
        if (ifdOffset == 0 || ifdOffset > stream.Length - 2)
            return false;
        stream.Position = ifdOffset;
        Span<byte> countBytes = stackalloc byte[2];
        if (stream.Read(countBytes) != 2)
            return false;
        ushort entryCount = ReadUInt16(countBytes, littleEndian);
        Span<byte> entry = stackalloc byte[12];
        for (int index = 0; index < entryCount; index++)
        {
            if (stream.Read(entry) != entry.Length)
                return false;
            ushort tag = ReadUInt16(entry[0..2], littleEndian);
            ushort type = ReadUInt16(entry[2..4], littleEndian);
            uint count = ReadUInt32(entry[4..8], littleEndian);
            if (tag != requestedTag || type != expectedType || count == 0)
                continue;

            int bytesPerValue = type switch { 2 => 1, 3 => 2, 12 => 8, _ => 0 };
            long byteCount = (long)count * bytesPerValue;
            if (bytesPerValue == 0 || byteCount > int.MaxValue)
                return false;
            value = new byte[(int)byteCount];
            if (byteCount <= 4)
            {
                entry[8..(8 + (int)byteCount)].CopyTo(value);
                return true;
            }

            uint valueOffset = ReadUInt32(entry[8..12], littleEndian);
            if (valueOffset > stream.Length - byteCount)
                return false;
            stream.Position = valueOffset;
            return stream.Read(value) == value.Length;
        }

        return false;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
