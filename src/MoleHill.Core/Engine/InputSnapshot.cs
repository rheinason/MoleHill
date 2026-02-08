using System.Runtime.InteropServices;

namespace MoleHill.Core.Engine;

/// <summary>
/// XY-hash fingerprint for change detection between solves.
/// Separates XY topology hash from Z values to enable Z-only update shortcut.
/// </summary>
public sealed class InputSnapshot
{
    /// <summary>Hash of XY coordinates + segment definitions + quality settings.</summary>
    public int XyHash { get; }

    /// <summary>Hash of Z values only.</summary>
    public int ZHash { get; }

    /// <summary>Z values in vertex order, for Z-update shortcut.</summary>
    public double[] ZValues { get; }

    public InputSnapshot(int xyHash, int zHash, double[] zValues)
    {
        XyHash = xyHash;
        ZHash = zHash;
        ZValues = zValues;
    }

    /// <summary>
    /// Build a snapshot from input data.
    /// </summary>
    public static InputSnapshot Create(double[] xyCoords, double[] zValues,
                                       int[] segments, QualitySettings quality)
    {
        // XY hash: XY coords + segments + quality
        var xyHasher = new HashCode();
        xyHasher.AddBytes(MemoryMarshal.AsBytes(xyCoords.AsSpan()));
        xyHasher.AddBytes(MemoryMarshal.AsBytes(segments.AsSpan()));

        Span<double> qualityBuf = stackalloc double[2];
        qualityBuf[0] = quality.MaxArea;
        qualityBuf[1] = quality.MinAngle;
        xyHasher.AddBytes(MemoryMarshal.AsBytes(qualityBuf));

        int xyHash = xyHasher.ToHashCode();

        // Z hash
        var zHasher = new HashCode();
        zHasher.AddBytes(MemoryMarshal.AsBytes(zValues.AsSpan()));
        int zHash = zHasher.ToHashCode();

        return new InputSnapshot(xyHash, zHash, (double[])zValues.Clone());
    }
}
