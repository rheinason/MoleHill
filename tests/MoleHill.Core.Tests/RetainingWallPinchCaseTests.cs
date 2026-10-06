using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public class RetainingWallPinchCaseTests
{
    private readonly ITestOutputHelper _output;

    public RetainingWallPinchCaseTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void QualityWallPatch_NonManifoldInput_DeclinesWithoutChangingInput()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 0, 10, 0 };
        int[] faces = { 0, 1, 2, 0, 1, 2, 0, 1, 2 };
        Assert.False(MeshConstraintTopologyInserter.TryInsertQualityWallPatch(vertices, faces,
            new[] { Rail((0, 4, 0), (5, 4, 2)) }, 0.01, out var resultVertices, out var resultFaces, out _));
        Assert.Equal(vertices, resultVertices);
        Assert.Equal(faces, resultFaces);
    }

    [Fact]
    public void QualityWallPatch_AboveFaceBudget_DeclinesBeforeTriangulation()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 0, 10, 0 };
        int[] faces = new int[4097 * 3];
        Assert.False(MeshConstraintTopologyInserter.TryInsertQualityWallPatch(vertices, faces,
            new[] { Rail((0, 4, 0), (5, 4, 2)) }, 0.01, out _, out _, out var message));
        Assert.Contains("budget", message);
    }

    // Terrain-1-20260917-225237-848a8059: the exported pre-wall TIN and wall rails.
    // This deliberately exercises the Core inserter without a Rhino runtime or the later Remesh card.
    [Fact]
    public void TryInsert_CopiedWallCase_CharacterizesQualityOfInsertionAndAlternatives()
    {
        double[] vertices =
        {
            0, -14.376288, 2.95, 2.0114703, -14.376288, 2.8695412,
            11.059209, -14.376288, 2.6433477, 26, -14.376288, 2.2698278,
            0, 12.29018, 2.95, 2.0114703, 12.29018, 2.8695412,
            11.059209, 12.29018, 2.6433477, 26, 12.29018, 2.2698278,
            0, 30.238869, 2.95, 2.0114703, 30.238869, 2.8695412,
            11.059209, 30.238869, 2.6433477, 26, 30.238869, 2.2698278,
            17.703857, 8, 2.4772315, 17.703857, 0, 2.4772315,
            11, 0, 2.075, 11, 8, 2.075
        };
        int[] faces =
        {
            14, 0, 1, 4, 0, 14, 13, 15, 14, 14, 1, 2,
            15, 5, 14, 5, 8, 4, 8, 5, 9, 5, 6, 9,
            5, 4, 14, 6, 5, 15, 15, 13, 12, 3, 13, 2,
            3, 7, 13, 12, 13, 7, 6, 15, 12, 9, 6, 10,
            7, 10, 6, 10, 7, 11, 7, 6, 12, 2, 13, 14
        };
        ConstraintPolyline[] rails =
        {
            Rail((-0.2, 0, 2.95), (-0.2, 8, 2.95)),
            Rail((0, 0, 1.8), (0, 8, 1.8)),
            Rail((0, 0, 1.8), (11, 0, 2.075), (17.703858159219195, 0, 2.4772314895531515)),
            Rail((0, -0.2, 2.95), (2.011470431092811, -0.2, 2.869541182756288),
                (11.059208693155119, -0.2, 2.64334772620473), (17.70385815921919, -0.2, 2.477231489553128)),
            Rail((0, 8, 1.8), (11, 8, 2.075), (17.703858159219195, 8, 2.4772314895531515)),
            Rail((0, 8.2, 2.95), (2.011470431092811, 8.2, 2.869541182756288),
                (11.059208693155119, 8.2, 2.64334772620473), (17.70385815921919, 8.2, 2.477231489553128))
        };

        AngleSummary before = Measure(vertices, faces);
        bool inserted = MeshConstraintTopologyInserter.TryInsert(
            vertices, vertices.Length / 3, faces, faces.Length / 3, rails, 0.01,
            out double[] outputVertices, out _, out int[] outputFaces, out _, out string? error);
        Assert.True(inserted, error);
        Assert.Equal(37, outputVertices.Length / 3);
        Assert.Equal(58, outputFaces.Length / 3);
        AngleSummary after = Measure(outputVertices, outputFaces);
        _output.WriteLine($"Before: {before}; after insertion: {after}; {outputVertices.Length / 3} vertices, {outputFaces.Length / 3} faces");

        SurfaceRemesher.Result rebuilt = SurfaceRemesher.Remesh(
            vertices, faces, rails,
            new SurfaceRemesher.Options
            {
                Tolerance = 0.01,
                PreferReducedInteriorSeed = true,
                AddReducedInteriorGuideSeeds = false,
                AddConstraintCorridorSeeds = false,
                ConstraintInsertionOnly = true
            });
        Assert.True(rebuilt.Success, rebuilt.Warning);
        AngleSummary rebuiltAngles = Measure(rebuilt.Vertices, rebuilt.Faces);
        _output.WriteLine($"Existing constrained fallback: {rebuiltAngles}; {rebuilt.Vertices.Length / 3} vertices, {rebuilt.Faces.Length / 3} faces");

        SurfaceRemesher.Result qualityRebuild = SurfaceRemesher.Remesh(
            vertices, faces, rails,
            new SurfaceRemesher.Options
            {
                Tolerance = 0.01,
                RequestedEdgeLength = 1,
                MinAngle = 20,
                PreferReducedInteriorSeed = true,
                AddReducedInteriorGuideSeeds = false,
                AddConstraintCorridorSeeds = false
            });
        _output.WriteLine($"Quality constrained rebuild: success={qualityRebuild.Success}, warning={qualityRebuild.Warning?.Split('.')[0]}; " +
                          $"{Measure(qualityRebuild.Vertices, qualityRebuild.Faces)}; " +
                          $"{qualityRebuild.Vertices.Length / 3} vertices, {qualityRebuild.Faces.Length / 3} faces");

        LocalMeshRefiner.Result localRefine = LocalMeshRefiner.Refine(
            outputVertices, outputFaces, rails,
            new LocalMeshRefiner.Options { TargetEdgeLength = 1, Tolerance = 0.01, DoFlips = true });
        Assert.True(localRefine.Success, localRefine.Warning);
        _output.WriteLine($"Local split/flip: {Measure(localRefine.Vertices, localRefine.Faces)}; " +
                          $"{localRefine.Vertices.Length / 3} vertices, {localRefine.Faces.Length / 3} faces; " +
                          $"{localRefine.AddedVertices} added, {localRefine.Flips} flips");

        Assert.True(after.BelowFive > before.BelowFive,
            $"Before: {before}; after insertion: {after}");
        Assert.True(after.WorstDegrees < before.WorstDegrees,
            $"Before: {before}; after insertion: {after}");

        foreach (int rings in new[] { 0, 1, 2 })
        {
            bool ok = MeshConstraintTopologyInserter.TryBuildWallPatchCandidate(vertices, faces, rails, 0.01,
                0, out var patchVertices, out var patchFaces, out var patchError, rings);
            Assert.True(ok, patchError);
            _output.WriteLine($"Patch rings={rings}: {Measure(patchVertices, patchFaces)}; " +
                $"{patchVertices.Length / 3} vertices, {patchFaces.Length / 3} faces");
            AssertContinuousRectangle(patchVertices, patchFaces);
            if (rings == 2)
            {
                var quality = Measure(patchVertices, patchFaces);
                Assert.True(quality.WorstDegrees >= 19.99);
                Assert.Equal(0, quality.BelowFive);
                AssertRailElevations(patchVertices, rails);
                AssertRailEdges(patchVertices, patchFaces, rails);
                foreach (int iterations in new[] { 3, 5 })
                {
                    var remeshed = IsotropicRemesher.Remesh(patchVertices, patchFaces, rails,
                        new IsotropicRemesher.Options { TargetEdgeLength = 1, Tolerance = 0.01,
                            CreaseAngleDeg = 30, WallFaceMinSlopeDeg = 70, Iterations = iterations });
                    Assert.True(remeshed.Success, remeshed.Warning);
                    _output.WriteLine($"Patch then {iterations} remesh iterations: {Measure(remeshed.Vertices, remeshed.Faces)}");
                    AssertContinuousRectangle(remeshed.Vertices, remeshed.Faces);
                    Assert.Equal(0, Measure(remeshed.Vertices, remeshed.Faces).BelowFive);
                    AssertRailElevations(remeshed.Vertices, rails);
                }
            }
        }
        Assert.True(MeshConstraintTopologyInserter.TryInsertQualityWallPatch(vertices, faces, rails, 0.01,
            out var adaptiveVertices, out var adaptiveFaces, out var adaptiveMessage), adaptiveMessage);
        _output.WriteLine(adaptiveMessage);
        AssertContinuousRectangle(adaptiveVertices, adaptiveFaces);
        Assert.Equal(0, Measure(adaptiveVertices, adaptiveFaces).BelowFive);
        var persistent = rails.Concat(new[]
        {
            Rail((0, -14.376288, 2.95), (2.0114703, -14.376288, 2.8695412), (11.059209, -14.376288, 2.6433477), (26, -14.376288, 2.2698278)),
            Rail((0, 12.29018, 2.95), (2.0114703, 12.29018, 2.8695412), (11.059209, 12.29018, 2.6433477), (26, 12.29018, 2.2698278)),
            Rail((0, 30.238869, 2.95), (2.0114703, 30.238869, 2.8695412), (11.059209, 30.238869, 2.6433477), (26, 30.238869, 2.2698278)),
            Rail((11, 0, 2.075), (11, 8, 2.075)),
            Rail((17.703857, 0, 2.4772315), (17.703857, 8, 2.4772315))
        }).ToArray();
        Assert.True(MeshConstraintTopologyInserter.TryInsertQualityWallPatch(vertices, faces, persistent, 0.01,
            out var hostVertices, out var hostFaces, out var hostMessage), hostMessage);
        _output.WriteLine($"With upstream constraints: {hostMessage}; {Measure(hostVertices, hostFaces)}");
        AssertRailEdges(hostVertices, hostFaces, persistent);
        AssertRailElevations(hostVertices, persistent);
        foreach (int iterations in new[] { 3, 5 })
        {
            var remeshed = IsotropicRemesher.Remesh(hostVertices, hostFaces, persistent,
                new IsotropicRemesher.Options { TargetEdgeLength = 1, Tolerance = 0.01,
                    CreaseAngleDeg = 30, WallFaceMinSlopeDeg = 70, Iterations = iterations });
            Assert.True(remeshed.Success, remeshed.Warning);
            _output.WriteLine($"Upstream constraints, {iterations} iterations: {Measure(remeshed.Vertices, remeshed.Faces)}");
            AssertContinuousRectangle(remeshed.Vertices, remeshed.Faces);
            Assert.Equal(0, Measure(remeshed.Vertices, remeshed.Faces).BelowFive);
        }
    }

    private static void AssertRailEdges(double[] vertices, int[] faces, ConstraintPolyline[] rails)
    {
        var edges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        static long Key(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
        for (int i = 0; i < faces.Length; i += 3)
            for (int e = 0; e < 3; e++) edges.Add(Key(faces[i + e], faces[i + (e + 1) % 3]));
        foreach (var rail in rails)
        for (int s = 0; s < rail.PointCount - 1; s++)
        {
            int a = s * 3, b = a + 3;
            double dx = rail.Points[b] - rail.Points[a], dy = rail.Points[b + 1] - rail.Points[a + 1];
            var chain = new List<(double T, int Index)>();
            for (int i = 0; i < vertices.Length; i += 3)
            {
                double t = ((vertices[i] - rail.Points[a]) * dx + (vertices[i + 1] - rail.Points[a + 1]) * dy) / (dx * dx + dy * dy);
                if (t < -1e-6 || t > 1 + 1e-6) continue;
                if (Math.Abs(vertices[i] - rail.Points[a] - t * dx) < 1e-5 &&
                    Math.Abs(vertices[i + 1] - rail.Points[a + 1] - t * dy) < 1e-5) chain.Add((t, i / 3));
            }
            chain.Sort((x, y) => x.T.CompareTo(y.T));
            for (int i = 1; i < chain.Count; i++)
                Assert.Contains(Key(chain[i - 1].Index, chain[i].Index), edges);
        }
    }

    [Fact]
    public void PatchExperiment_RefinedWallBand_InterpolatesAuthoredWallSurface()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        var rails = new[] { Rail((0, 4, 0), (10, 4, 0)), Rail((0, 6, 2), (10, 6, 2)) };
        Assert.True(MeshConstraintTopologyInserter.TryBuildWallPatchCandidate(vertices, faces, rails, 1e-6,
            0, out var refined, out _, out var error, maxArea: 0.2), error);
        int insideBand = 0;
        for (int i = 0; i < refined.Length; i += 3)
        {
            double y = refined[i + 1];
            if (y <= 4 + 1e-8 || y >= 6 - 1e-8) continue;
            insideBand++;
            Assert.Equal(y - 4, refined[i + 2], 7);
        }
        Assert.True(insideBand > 0, "Exercise actual Steiner vertices inside the wall, not just rail vertices.");
        AssertRailElevations(refined, rails);
    }

    private static void AssertRailElevations(double[] vertices, ConstraintPolyline[] rails)
    {
        foreach (var rail in rails)
        for (int s = 0; s < rail.PointCount - 1; s++)
        {
            int a = s * 3, b = a + 3;
            double dx = rail.Points[b] - rail.Points[a], dy = rail.Points[b + 1] - rail.Points[a + 1];
            int sampled = 0;
            for (int i = 0; i < vertices.Length; i += 3)
            {
                double t = ((vertices[i] - rail.Points[a]) * dx + (vertices[i + 1] - rail.Points[a + 1]) * dy) / (dx * dx + dy * dy);
                if (t < -1e-6 || t > 1 + 1e-6) continue;
                if (Math.Abs(vertices[i] - rail.Points[a] - t * dx) > 1e-5 ||
                    Math.Abs(vertices[i + 1] - rail.Points[a + 1] - t * dy) > 1e-5) continue;
                sampled++;
                Assert.Equal(rail.Points[a + 2] + t * (rail.Points[b + 2] - rail.Points[a + 2]), vertices[i + 2], 1e-6);
            }
            // The left-hand rail is outside the supplied terrain and is intentionally clipped out.
            if (rail.Points[a] >= 0) Assert.True(sampled >= 2);
        }
    }

    private static void AssertContinuousRectangle(double[] vertices, int[] faces)
    {
        var edges = new Dictionary<long, int>(IndexedMeshTools.EdgeKeyComparer.Instance);
        double area = 0;
        for (int i = 0; i < faces.Length; i += 3)
        {
            int a = faces[i] * 3, b = faces[i + 1] * 3, c = faces[i + 2] * 3;
            double cross = (vertices[b] - vertices[a]) * (vertices[c + 1] - vertices[a + 1]) -
                           (vertices[b + 1] - vertices[a + 1]) * (vertices[c] - vertices[a]);
            Assert.True(cross > 1e-12, $"Degenerate or reversed face {i / 3}: ({vertices[a]}, {vertices[a+1]}) ({vertices[b]}, {vertices[b+1]}) ({vertices[c]}, {vertices[c+1]}); cross={cross}");
            area += cross / 2;
            for (int e = 0; e < 3; e++)
            {
                int u = faces[i + e], v = faces[i + (e + 1) % 3];
                long key = ((long)Math.Min(u, v) << 32) | (uint)Math.Max(u, v);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }
        Assert.Equal(26 * (30.238869 + 14.376288), area, 6);
        foreach (var edge in edges)
        {
            Assert.InRange(edge.Value, 1, 2);
            if (edge.Value == 2) continue;
            int a = (int)(edge.Key >> 32) * 3, b = (int)edge.Key * 3;
            bool outer = new[] { (0, 0.0), (0, 26.0), (1, -14.376288), (1, 30.238869) }
                .Any(bound => Math.Abs(vertices[a + bound.Item1] - bound.Item2) < 1e-7 &&
                              Math.Abs(vertices[b + bound.Item1] - bound.Item2) < 1e-7);
            Assert.True(outer, $"Interior open edge {a / 3}–{b / 3}");
        }
    }

    private static ConstraintPolyline Rail(params (double X, double Y, double Z)[] points)
    {
        double[] xyz = new double[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            xyz[i * 3] = points[i].X;
            xyz[i * 3 + 1] = points[i].Y;
            xyz[i * 3 + 2] = points[i].Z;
        }
        return new ConstraintPolyline(xyz, points.Length, false, true);
    }

    private static AngleSummary Measure(double[] vertices, int[] faces)
    {
        double worst = 180;
        int belowFive = 0;
        int belowOne = 0;
        for (int i = 0; i < faces.Length; i += 3)
        {
            int a = faces[i] * 3;
            int b = faces[i + 1] * 3;
            int c = faces[i + 2] * 3;
            double ab = Distance(vertices, a, b);
            double bc = Distance(vertices, b, c);
            double ca = Distance(vertices, c, a);
            double min = Math.Min(Angle(ab, ca, bc), Math.Min(Angle(ab, bc, ca), Angle(bc, ca, ab)));
            worst = Math.Min(worst, min);
            if (min < 5) belowFive++;
            if (min < 1) belowOne++;
        }
        return new AngleSummary(worst, belowFive, belowOne);
    }

    private static double Distance(double[] vertices, int a, int b)
    {
        double dx = vertices[a] - vertices[b];
        double dy = vertices[a + 1] - vertices[b + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Angle(double adjacentA, double adjacentB, double opposite) =>
        Math.Acos(Math.Clamp((adjacentA * adjacentA + adjacentB * adjacentB - opposite * opposite) /
                             (2 * adjacentA * adjacentB), -1, 1)) * 180 / Math.PI;

    private readonly record struct AngleSummary(double WorstDegrees, int BelowFive, int BelowOne);

    [Fact]
    public void TryBuildWallPatchCandidate_OverFaceBudget_AbandonsCandidateAndReportsBudget()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        var rails = new[] { Rail((0, 4, 0), (10, 4, 0)), Rail((0, 6, 2), (10, 6, 2)) };

        Assert.False(MeshConstraintTopologyInserter.TryBuildWallPatchCandidate(vertices, faces, rails, 1e-6,
            0, out var outputVertices, out var outputFaces, out var error, out bool exceeded,
            maxArea: 0.2, maxOutputFaces: 10));

        Assert.True(exceeded);
        Assert.Contains("exceeds", error);
        Assert.Equal(vertices, outputVertices);
        Assert.Equal(faces, outputFaces);
    }

    // Golden fingerprints of the copied wall case, captured from the linear-scan Sample() before the
    // reference faces were indexed. Indexing is a speed change only: every candidate must be identical.
    // Vertex sums re-captured 2026-10-06 when edge splits began resolving exactly onto their edge: counts and
    // face sums unchanged, a few coordinates moved by micrometres.
    [Theory]
    [InlineData(0, 3685227933116L, 585032642L, 402, 775)]
    [InlineData(1, 4100439791552L, 597149611L, 408, 779)]
    [InlineData(2, 3234618732050L, 502700981L, 382, 731)]
    public void TryBuildWallPatchCandidate_CopiedWallCase_OutputUnchangedByIndexedSampling(
        int rings, long expectedVertexChecksum, long expectedFaceChecksum, int expectedVertexCount, int expectedFaceCount)
    {
        double[] vertices =
        {
            0, -14.376288, 2.95, 2.0114703, -14.376288, 2.8695412,
            11.059209, -14.376288, 2.6433477, 26, -14.376288, 2.2698278,
            0, 12.29018, 2.95, 2.0114703, 12.29018, 2.8695412,
            11.059209, 12.29018, 2.6433477, 26, 12.29018, 2.2698278,
            0, 30.238869, 2.95, 2.0114703, 30.238869, 2.8695412,
            11.059209, 30.238869, 2.6433477, 26, 30.238869, 2.2698278,
            17.703857, 8, 2.4772315, 17.703857, 0, 2.4772315,
            11, 0, 2.075, 11, 8, 2.075
        };
        int[] faces =
        {
            14, 0, 1, 4, 0, 14, 13, 15, 14, 14, 1, 2,
            15, 5, 14, 5, 8, 4, 8, 5, 9, 5, 6, 9,
            5, 4, 14, 6, 5, 15, 15, 13, 12, 3, 13, 2,
            3, 7, 13, 12, 13, 7, 6, 15, 12, 9, 6, 10,
            7, 10, 6, 10, 7, 11, 7, 6, 12, 2, 13, 14
        };
        ConstraintPolyline[] rails =
        {
            Rail((-0.2, 0, 2.95), (-0.2, 8, 2.95)),
            Rail((0, 0, 1.8), (0, 8, 1.8)),
            Rail((0, 0, 1.8), (11, 0, 2.075), (17.703858159219195, 0, 2.4772314895531515)),
            Rail((0, -0.2, 2.95), (2.011470431092811, -0.2, 2.869541182756288),
                (11.059208693155119, -0.2, 2.64334772620473), (17.70385815921919, -0.2, 2.477231489553128)),
            Rail((0, 8, 1.8), (11, 8, 2.075), (17.703858159219195, 8, 2.4772314895531515)),
            Rail((0, 8.2, 2.95), (2.011470431092811, 8.2, 2.869541182756288),
                (11.059208693155119, 8.2, 2.64334772620473), (17.70385815921919, 8.2, 2.477231489553128))
        };

        Assert.True(MeshConstraintTopologyInserter.TryBuildWallPatchCandidate(vertices, faces, rails, 0.01,
            0, out var patchVertices, out var patchFaces, out var error, rings), error);

        long vertexChecksum = 0;
        for (int i = 0; i < patchVertices.Length; i++)
            vertexChecksum += (long)Math.Round(patchVertices[i] * 1e6) * (i + 1);
        long faceChecksum = 0;
        for (int i = 0; i < patchFaces.Length; i++)
            faceChecksum += (long)patchFaces[i] * (i + 1);

        _output.WriteLine($"[InlineData({rings}, {vertexChecksum}L, {faceChecksum}L, {patchVertices.Length / 3}, {patchFaces.Length / 3})]");
        Assert.Equal(expectedVertexCount, patchVertices.Length / 3);
        Assert.Equal(expectedFaceCount, patchFaces.Length / 3);
        Assert.Equal(expectedVertexChecksum, vertexChecksum);
        Assert.Equal(expectedFaceChecksum, faceChecksum);
    }
}

