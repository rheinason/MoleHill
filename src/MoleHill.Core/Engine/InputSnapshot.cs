using System.Runtime.InteropServices;

namespace MoleHill.Core.Engine;

/// <summary>
/// XY-hash fingerprint for change detection between solves.
/// Separates XY topology hash from Z values to enable Z-only update shortcut.
/// </summary>
public sealed class InputSnapshot
{
    /// <summary>Hash of XY coordinates + segment definitions + quality settings + domain mode.</summary>
    public int XyHash { get; }

    /// <summary>Hash of Z values only.</summary>
    public int ZHash { get; }

    public InputSnapshot(int xyHash, int zHash)
    {
        XyHash = xyHash;
        ZHash = zHash;
    }

    /// <summary>
    /// Compute hash of XY coordinates + segment definitions + quality settings + domain mode.
    /// </summary>
    public static int ComputeXyHash(
        double[] xyCoords,
        int[] segments,
        QualitySettings quality,
        bool useConvexHull = true,
        double maxBoundaryEdgeLength = 0,
        BoundaryTrianglePeelSettings? boundaryPeelSettings = null)
    {
        var xyHasher = new HashCode();
        xyHasher.AddBytes(MemoryMarshal.AsBytes(xyCoords.AsSpan()));
        xyHasher.AddBytes(MemoryMarshal.AsBytes(segments.AsSpan()));

        Span<double> qualityBuf = stackalloc double[2];
        qualityBuf[0] = quality.MaxArea;
        qualityBuf[1] = quality.MinAngle;
        xyHasher.AddBytes(MemoryMarshal.AsBytes(qualityBuf));
        xyHasher.Add(useConvexHull);
        BoundaryTrianglePeelSettings settings = boundaryPeelSettings
            ?? BoundaryTrianglePeelSettings.FromLegacyMaxBoundaryEdgeLength(maxBoundaryEdgeLength);
        xyHasher.Add(settings.Enabled);
        xyHasher.Add(settings.MaxBoundaryEdgeLength);
        xyHasher.Add(settings.MaxInteriorAngleDegrees);
        xyHasher.Add(settings.MaxSlopeAngleDegrees);

        return xyHasher.ToHashCode();
    }

    /// <summary>
    /// Compute hash of Z values only.
    /// </summary>
    public static int ComputeZHash(double[] zValues)
    {
        var zHasher = new HashCode();
        zHasher.AddBytes(MemoryMarshal.AsBytes(zValues.AsSpan()));
        return zHasher.ToHashCode();
    }
}
