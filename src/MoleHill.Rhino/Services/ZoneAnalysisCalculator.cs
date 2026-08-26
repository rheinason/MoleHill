using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class ZoneAnalysisCalculator
{
    public static ZoneAnalysisSummary Summarize(Guid zoneId, IReadOnlyList<Mesh> meshes)
    {
        var result = new ZoneAnalysisSummary { ZoneId = zoneId };
        double elevationWeightedSum = 0.0;
        double elevationWeight = 0.0;
        double slopeWeightedSum = 0.0;

        foreach (Mesh mesh in meshes)
        {
            if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out double[] vertices, out int[] faces, out _))
                continue;

            result.OutputCount++;
            result.TriangleCount += faces.Length / 3;
            result.SurfaceArea += AreaMassProperties.Compute(mesh)?.Area ?? 0.0;

            var slopes = SlopeAnalyzer.Summarize(
                vertices, mesh.Vertices.Count, faces, faces.Length / 3,
                SlopeAnalyzer.SlopeUnit.Percent);
            if (result.TriangleCount == faces.Length / 3)
            {
                result.SlopeMinPercent = slopes.Min;
                result.SlopeMaxPercent = slopes.Max;
            }
            else
            {
                result.SlopeMinPercent = Math.Min(result.SlopeMinPercent, slopes.Min);
                result.SlopeMaxPercent = Math.Max(result.SlopeMaxPercent, slopes.Max);
            }

            double minZ = double.MaxValue;
            double maxZ = double.MinValue;
            double planArea = 0.0;
            for (int face = 0; face < faces.Length / 3; face++)
            {
                int a = faces[face * 3];
                int b = faces[face * 3 + 1];
                int c = faces[face * 3 + 2];
                double ax = vertices[a * 3], ay = vertices[a * 3 + 1], az = vertices[a * 3 + 2];
                double bx = vertices[b * 3], by = vertices[b * 3 + 1], bz = vertices[b * 3 + 2];
                double cx = vertices[c * 3], cy = vertices[c * 3 + 1], cz = vertices[c * 3 + 2];
                double area = Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) * 0.5;
                double averageZ = (az + bz + cz) / 3.0;
                planArea += area;
                elevationWeightedSum += averageZ * area;
                elevationWeight += area;
                slopeWeightedSum += SlopeAnalyzerFaceSlope(vertices, a, b, c) * area;
                minZ = Math.Min(minZ, Math.Min(az, Math.Min(bz, cz)));
                maxZ = Math.Max(maxZ, Math.Max(az, Math.Max(bz, cz)));
            }

            result.PlanArea += planArea;
            if (minZ != double.MaxValue)
            {
                result.ElevationMinZ = result.OutputCount == 1 ? minZ : Math.Min(result.ElevationMinZ, minZ);
                result.ElevationMaxZ = result.OutputCount == 1 ? maxZ : Math.Max(result.ElevationMaxZ, maxZ);
            }
        }

        result.ElevationAverageZ = elevationWeight > 0 ? elevationWeightedSum / elevationWeight : 0.0;
        result.SlopeAveragePercent = elevationWeight > 0 ? slopeWeightedSum / elevationWeight : 0.0;
        return result;
    }

    private static double SlopeAnalyzerFaceSlope(double[] vertices, int a, int b, int c)
    {
        double ax = vertices[a * 3], ay = vertices[a * 3 + 1], az = vertices[a * 3 + 2];
        double bx = vertices[b * 3], by = vertices[b * 3 + 1], bz = vertices[b * 3 + 2];
        double cx = vertices[c * 3], cy = vertices[c * 3 + 1], cz = vertices[c * 3 + 2];
        double ux = bx - ax, uy = by - ay, uz = bz - az;
        double vx = cx - ax, vy = cy - ay, vz = cz - az;
        double nx = uy * vz - uz * vy;
        double ny = uz * vx - ux * vz;
        double nz = ux * vy - uy * vx;
        double horizontal = Math.Sqrt(nx * nx + ny * ny);
        return horizontal <= double.Epsilon ? 0.0 : Math.Abs(nz) <= double.Epsilon ? double.PositiveInfinity : Math.Abs(horizontal / nz) * 100.0;
    }
}
