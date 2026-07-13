namespace MoleHill.Core.Analysis;

/// <summary>
/// Fast, sampled mesh-quality check used to warn before vertex-based smoothing or sculpting is
/// applied to an uneven TIN. Inputs use the Core flat-array convention.
/// </summary>
public static class MeshRegularityAnalyzer
{
    private const double SkinnyAngleDegrees = 8.0;
    private const int SparseFaceThreshold = 250;
    private const double SparseDiagonalEdgeRatio = 12.0;

    public static MeshRegularitySummary Analyze(
        double[] vertices,
        int[] faces,
        int maxSampleFaces = 2048)
    {
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        if (vertexCount == 0 || faceCount == 0 || maxSampleFaces <= 0)
            return new MeshRegularitySummary(faceCount, 0, 0, 0, 0);

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        double maxZ = double.NegativeInfinity;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            double z = vertices[i * 3 + 2];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        double dx = maxX - minX;
        double dy = maxY - minY;
        double dz = maxZ - minZ;
        double diagonal = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        int sampleCount = Math.Min(faceCount, maxSampleFaces);
        var representativeEdges = new double[sampleCount];
        int skinnyCount = 0;

        for (int sample = 0; sample < sampleCount; sample++)
        {
            int faceIndex = (int)((long)sample * faceCount / sampleCount);
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            if ((uint)a >= vertexCount || (uint)b >= vertexCount || (uint)c >= vertexCount)
            {
                skinnyCount++;
                continue;
            }

            double ab = EdgeLength(vertices, a, b);
            double bc = EdgeLength(vertices, b, c);
            double ca = EdgeLength(vertices, c, a);
            representativeEdges[sample] = (ab + bc + ca) / 3.0;
            if (MinimumAngleDegrees(ab, bc, ca) < SkinnyAngleDegrees)
                skinnyCount++;
        }

        Array.Sort(representativeEdges);
        double medianEdge = representativeEdges[representativeEdges.Length / 2];
        return new MeshRegularitySummary(faceCount, sampleCount, skinnyCount, medianEdge, diagonal);
    }

    private static double EdgeLength(double[] vertices, int a, int b)
    {
        double dx = vertices[a * 3] - vertices[b * 3];
        double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
        double dz = vertices[a * 3 + 2] - vertices[b * 3 + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double MinimumAngleDegrees(double a, double b, double c)
    {
        if (a <= 1e-12 || b <= 1e-12 || c <= 1e-12)
            return 0;

        double angleA = AngleDegrees(a, c, b);
        double angleB = AngleDegrees(a, b, c);
        return Math.Min(angleA, Math.Min(angleB, 180.0 - angleA - angleB));
    }

    private static double AngleDegrees(double adjacentA, double adjacentB, double opposite)
    {
        double cosine = (adjacentA * adjacentA + adjacentB * adjacentB - opposite * opposite) /
                        (2.0 * adjacentA * adjacentB);
        return Math.Acos(Math.Clamp(cosine, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    public static bool IsVerySparse(MeshRegularitySummary summary) =>
        summary.FaceCount < SparseFaceThreshold ||
        summary.Diagonal > 0 && summary.MedianEdgeLength > summary.Diagonal / SparseDiagonalEdgeRatio;

    public static bool HasVerySkinnyTriangles(MeshRegularitySummary summary) =>
        summary.SampledFaceCount > 0 &&
        summary.SkinnyFaceCount >= Math.Max(1, summary.SampledFaceCount / 100);
}

public readonly record struct MeshRegularitySummary(
    int FaceCount,
    int SampledFaceCount,
    int SkinnyFaceCount,
    double MedianEdgeLength,
    double Diagonal)
{
    public double SkinnyFraction => SampledFaceCount == 0 ? 0 : (double)SkinnyFaceCount / SampledFaceCount;
}
