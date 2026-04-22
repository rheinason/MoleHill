using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal enum GradingPatchKind
{
    Pad,
    Path
}

internal sealed class GradingPatch
{
    public required string OwnerKey { get; init; }

    public required GradingPatchKind Kind { get; init; }

    public required double Priority { get; init; }

    public required double[] OwnedRegionLoopXy { get; init; }

    public double[]? DaylightLoopXy { get; init; }

    public double[]? StitchLoopXy { get; init; }

    public Bounds2D DirtyBounds { get; init; }

    public bool UsesFallbackBand { get; init; }

    public static Bounds2D ComputeBounds(double[] loopXy)
    {
        if (loopXy.Length < 2)
            return new Bounds2D(0.0, 0.0, 0.0, 0.0);

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        int vertexCount = loopXy.Length / 2;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = loopXy[i * 2];
            double y = loopXy[i * 2 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return new Bounds2D(minX, maxX, minY, maxY);
    }
}
