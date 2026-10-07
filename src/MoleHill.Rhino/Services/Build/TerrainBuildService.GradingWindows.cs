using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Shared;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Windowed grading for the Grade Pad and Grade Path stages and the Retaining Wall's rail insertion
/// (<see cref="GradingWindows"/>): each group of
/// items is graded on the faces within its reach, and a window whose input is unchanged since the last run
/// is taken from the stage's memo. An edit to the terrain away from the items, or to one of them, re-grades
/// only the windows it touches, and the result is exactly a cold build's.
/// </summary>
internal sealed partial class TerrainBuildService
{
    /// <summary>Faces of reach around a rail, in local face sizes: the quality patch grows up to six rings.</summary>
    private const double WallInsertRings = 8.0;

    /// <summary>
    /// <see cref="RetainingWallStage.InsertWallConstraintsCore"/> window by window: each rail's window is the faces within eight of
    /// its local face sizes (the quality patch grows up to six rings of faces around the faces a rail crosses),
    /// and a window unchanged since the last build is reused. Returns false, with nothing written to
    /// <paramref name="build"/>, whenever it cannot stand in for the whole-mesh insertion exactly (a window that
    /// declines, or one that would not weld back), so the caller runs that insertion instead.
    /// </summary>
    internal static bool TryInsertWallConstraintsWindowed(
        RhinoMesh mesh,
        IReadOnlyList<ConstraintPolyline> wallConstraints,
        double tolerance,
        TerrainBuildResult build,
        bool useQualityPatch,
        TerrainRuntimeCache runtimeCache,
        string memoKey,
        out RhinoMesh insertedMesh)
    {
        insertedMesh = mesh;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out _))
            return false;

        var rails = new List<GradingWindows.Reach>(wallConstraints.Count);
        foreach (ConstraintPolyline rail in wallConstraints)
        {
            var xy = new double[rail.PointCount * 2];
            for (int k = 0; k < rail.PointCount; k++)
            {
                xy[k * 2] = rail.Points[k * 3];
                xy[k * 2 + 1] = rail.Points[k * 3 + 1];
            }

            rails.Add(new GradingWindows.Reach(xy, rail.PointCount, rail.IsClosed, Filled: false, Radius: 0.0));
        }

        double[] local = GradingWindows.LongestFaceWithin(vertices, faces, faceCount, rails);
        for (int i = 0; i < rails.Count; i++)
            rails[i] = rails[i] with { Radius = Math.Max(tolerance * 100.0, WallInsertRings * local[i]) };
        List<GradingWindows.Reach> reach = GradingWindows.WithMargins(vertices, faces, faceCount, rails, tolerance * 100.0);

        // A rail whose reach covers the whole terrain makes one window of everything, and windowing it only
        // adds assignment, keys and stitching: about 150 ms of a 400 ms wall edit on a contour TIN whose faces
        // are tens of metres long. The whole-mesh insertion gives the same result, so hand over to it.
        if (GradingWindows.AnyReachCoversAll(vertices, vertexCount, reach))
            return false;

        IReadOnlyList<ConstraintPolyline>? qualityConstraints = useQualityPatch
            ? CombineConstraints(CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints), wallConstraints)
            : null;
        List<ConstraintPolyline> Within(IEnumerable<ConstraintPolyline> constraints, (double MinX, double MinY, double MaxX, double MaxY) box) =>
            constraints.Where(c => GradingWindows.PointsOverlap(c.Points, c.PointCount, box)).ToList();

        runtimeCache.GradingWindowMemos.TryGetValue(memoKey, out GradingWindows.Memo? previous);
        var next = new GradingWindows.Memo();
        GradingWindows.Outcome outcome = GradingWindows.Grade(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), reach, margin: 0.0, (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double MinX, double MinY, double MaxX, double MaxY) box,
                out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureDiagnostics) =>
            {
                failurePolylines = Array.Empty<OutputPolyline>();
                failureDiagnostics = Array.Empty<GradingDiagnostic>();
                var messages = new List<string>();
                if (!RetainingWallStage.InsertWallConstraintsCore(
                        wv, wvc, wf, wfc, Within(wallConstraints, box), qualityConstraints == null ? null : Within(qualityConstraints, box),
                        tolerance, afterCombinedRemeshFailed: false, messages, out double[] outV, out int[] outF))
                {
                    error = string.Join(" ", messages);
                    return null;
                }

                error = null;
                return new GradingResult(outV, outV.Length / 3, outF, outF.Length / 3, 0, 0, Array.Empty<double>(), 0, diagnostics: messages);
            }, (items, minX, minY, maxX, maxY, low, high) =>
            {
                var box = (minX, minY, maxX, maxY);
                void Add(double value)
                {
                    low.Add(value);
                    high.Add(value);
                }

                Add(tolerance);
                Add(useQualityPatch ? 1 : 0);
                foreach (var set in new[] { Within(wallConstraints, box), qualityConstraints == null ? new List<ConstraintPolyline>() : Within(qualityConstraints, box) })
                {
                    Add(set.Count);
                    foreach (ConstraintPolyline c in set)
                    {
                        Add(c.PointCount);
                        Add(c.IsClosed ? 1 : 0);
                        Add(c.PreserveInputElevation ? 1 : 0);
                        for (int k = 0; k < c.PointCount * 3; k++)
                            Add(c.Points[k]);
                    }
                }
            }, previous, next);

        if (outcome.NeedsWholeMesh || outcome.Errors.Count > 0 || outcome.Result == null)
            return false;

        runtimeCache.GradingWindowMemos[memoKey] = next;
        build.Diagnostics.AddRange(outcome.Result.Diagnostics.Distinct());
        build.Diagnostics.Add($"Retaining Wall inserted its rails in {outcome.WindowCount:N0} window(s); {outcome.ReusedWindows:N0} unchanged since the last build ({outcome.Timings}).");
        insertedMesh = BuildMeshFromArrays(outcome.Result.Vertices, outcome.Result.Faces);
        return true;
    }
}
