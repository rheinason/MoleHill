using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace TopoTIN.Core.Engine;

/// <summary>
/// XY-hash fingerprint for change detection between solves.
/// Separates XY topology hash from Z values to enable Z-only update shortcut.
/// </summary>
public sealed class InputSnapshot
{
    /// <summary>Hash of XY coordinates + segment definitions + quality settings.</summary>
    public ulong XyHash { get; }

    /// <summary>Hash of Z values only.</summary>
    public ulong ZHash { get; }

    /// <summary>Z values in vertex order, for Z-update shortcut.</summary>
    public double[] ZValues { get; }

    public InputSnapshot(ulong xyHash, ulong zHash, double[] zValues)
    {
        XyHash = xyHash;
        ZHash = zHash;
        ZValues = zValues;
    }

    /// <summary>
    /// Build a snapshot from input data.
    /// </summary>
    /// <param name="xyCoords">Flat array [x0,y0, x1,y1, …] of all vertices (merged points + breakline vertices).</param>
    /// <param name="zValues">Z values in the same vertex order.</param>
    /// <param name="segments">Flat array [a0,b0, a1,b1, …] of segment index pairs.</param>
    /// <param name="quality">Quality settings.</param>
    public static InputSnapshot Create(double[] xyCoords, double[] zValues,
                                       int[] segments, QualitySettings quality)
    {
        // XY hash: XY coords + segments + quality
        var xyHasher = new XxHash64();
        xyHasher.Append(MemoryMarshal.AsBytes(xyCoords.AsSpan()));
        xyHasher.Append(MemoryMarshal.AsBytes(segments.AsSpan()));

        Span<double> qualityBuf = stackalloc double[2];
        qualityBuf[0] = quality.MaxArea;
        qualityBuf[1] = quality.MinAngle;
        xyHasher.Append(MemoryMarshal.AsBytes(qualityBuf));

        ulong xyHash = xyHasher.GetCurrentHashAsUInt64();

        // Z hash
        var zHasher = new XxHash64();
        zHasher.Append(MemoryMarshal.AsBytes(zValues.AsSpan()));
        ulong zHash = zHasher.GetCurrentHashAsUInt64();

        return new InputSnapshot(xyHash, zHash, (double[])zValues.Clone());
    }
}
