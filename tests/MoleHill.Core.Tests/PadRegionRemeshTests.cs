using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Tests;

public class PadRegionRemeshTests
{
    [Fact]
    public void GradeWithRegionRemesh_SinglePadOnSlope_IsWatertightFlatAndSpikeFree()
    {
        // Gently sloped dense grid terrain: Z = 0.15 * x over [0,40]^2, spacing 2.
        const double spacing = 2.0;
        int n = 21;
        var vertices = new double[n * n * 3];
        int idx = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double x = i * spacing;
                double y = j * spacing;
                vertices[idx++] = x;
                vertices[idx++] = y;
                vertices[idx++] = 0.15 * x;
            }
        }

        var faceList = new List<int>();
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = (j * n) + i;
                int v10 = v00 + 1;
                int v01 = v00 + n;
                int v11 = v01 + 1;
                faceList.Add(v00); faceList.Add(v10); faceList.Add(v11);
                faceList.Add(v00); faceList.Add(v11); faceList.Add(v01);
            }
        }

        int[] faces = faceList.ToArray();
        int faceCount = faces.Length / 3;
        int vertexCount = vertices.Length / 3;

        // Flat pad at Z = 3.0 (mid-slope): cuts where x > 20, fills where x < 20.
        const double padZ = 3.0;
        var pad = PadGrader.PadBoundary.CreatePlanar(
            new[]
            {
                15.0, 15.0, padZ, 25.0, 15.0, padZ, 25.0, 25.0, padZ, 15.0, 25.0, padZ,
            },
            4,
            0.0,
            0.0,
            padZ,
            slopeAngleDeg: 33.0);

        GradingResult? result = PadGrader.GradeWithRegionRemesh(
            vertices, vertexCount, faces, faceCount,
            new[] { pad }, Array.Empty<PadGrader.LockCurve>(),
            modelTolerance: 1e-4, terrainDetailSize: spacing, out string? errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Contains(result!.Diagnostics, d => d.Contains("region remesh", StringComparison.OrdinalIgnoreCase));

        // Watertight + manifold + single boundary (same as input terrain) — by construction.
        MeshTopologyValidator.BoundaryGraphAnalysis inputTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);
        Assert.Equal(inputTopology.BoundaryComponentCount, topology.BoundaryComponentCount);

        double[] v = result.Vertices;

        // Pad top is flat at the pad plane.
        for (int f = 0; f < result.FaceCount; f++)
        {
            int a = result.Faces[f * 3], b = result.Faces[f * 3 + 1], c = result.Faces[f * 3 + 2];
            if (Inside(v, a, pad) && Inside(v, b, pad) && Inside(v, c, pad))
            {
                Assert.Equal(padZ, v[a * 3 + 2], 3);
                Assert.Equal(padZ, v[b * 3 + 2], 3);
                Assert.Equal(padZ, v[c * 3 + 2], 3);
            }
        }

        // The region was actually graded (a batter exists between pad plane and terrain), and no
        // batter face is a near-vertical spike (distance-field slopes are ~33 degrees, not 90).
        bool batterFound = false;
        double maxSlope = 0.0;
        for (int f = 0; f < result.FaceCount; f++)
        {
            int a = result.Faces[f * 3], b = result.Faces[f * 3 + 1], c = result.Faces[f * 3 + 2];
            bool allInPad = Inside(v, a, pad) && Inside(v, b, pad) && Inside(v, c, pad);
            if (allInPad)
                continue;

            double slope = FaceSlopeDegrees(v, a, b, c);
            if (slope > maxSlope)
                maxSlope = slope;

            // A face touching the footprint but not entirely inside is batter.
            if (Inside(v, a, pad) || Inside(v, b, pad) || Inside(v, c, pad))
                batterFound = true;
        }

        Assert.True(batterFound, "Expected a graded batter band around the pad.");
        Assert.True(maxSlope < 50.0, $"Expected spike-free slopes (~33 deg target); max non-pad face slope was {maxSlope:F1} deg.");
    }

    [Fact]
    public void GradeWithRegionRemesh_UnsetTerrainDetail_TerminatesAndIsWatertight()
    {
        // terrainDetailSize defaults to 0 when called from Rhino. The interior grid spacing must be
        // derived from the rim and hard-capped, or the seeding loop emits billions of points and hangs.
        const double spacing = 2.0;
        int n = 21;
        var vertices = new double[n * n * 3];
        int idx = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                vertices[idx++] = i * spacing;
                vertices[idx++] = j * spacing;
                vertices[idx++] = 0.15 * (i * spacing);
            }
        }

        var faceList = new List<int>();
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = (j * n) + i, v10 = v00 + 1, v01 = v00 + n, v11 = v01 + 1;
                faceList.Add(v00); faceList.Add(v10); faceList.Add(v11);
                faceList.Add(v00); faceList.Add(v11); faceList.Add(v01);
            }
        }

        int[] faces = faceList.ToArray();
        var pad = PadGrader.PadBoundary.CreatePlanar(
            new[] { 15.0, 15.0, 3.0, 25.0, 15.0, 3.0, 25.0, 25.0, 3.0, 15.0, 25.0, 3.0 },
            4, 0.0, 0.0, 3.0, slopeAngleDeg: 33.0);

        GradingResult? result = PadGrader.GradeWithRegionRemesh(
            vertices, vertices.Length / 3, faces, faces.Length / 3,
            new[] { pad }, Array.Empty<PadGrader.LockCurve>(),
            modelTolerance: 1e-4, terrainDetailSize: 0.0, out string? errorMessage);

        Assert.True(result != null, errorMessage);
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result!.Faces, result.FaceCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);
    }

    private static bool Inside(double[] v, int index, PadGrader.PadBoundary pad) =>
        Geometry2D.PointInPolygon(v[index * 3], v[index * 3 + 1], pad.XyVertices, pad.VertexCount);

    private static double FaceSlopeDegrees(double[] v, int a, int b, int c)
    {
        double ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2];
        double bx = v[b * 3], by = v[b * 3 + 1], bz = v[b * 3 + 2];
        double cx = v[c * 3], cy = v[c * 3 + 1], cz = v[c * 3 + 2];
        double ux = bx - ax, uy = by - ay, uz = bz - az;
        double wx = cx - ax, wy = cy - ay, wz = cz - az;
        double nx = (uy * wz) - (uz * wy);
        double ny = (uz * wx) - (ux * wz);
        double nz = (ux * wy) - (uy * wx);
        double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (len < 1e-12)
            return 0.0;

        return Math.Acos(Math.Min(1.0, Math.Abs(nz) / len)) * 180.0 / Math.PI;
    }
}
