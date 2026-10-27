using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class SurfaceStripGrader
{
    /// <summary>
    /// Grades the surfaces window by window (<see cref="GradingWindows"/>), as
    /// <see cref="Grade(IndexedTriMesh, IReadOnlyList{SurfaceDefinition}, IReadOnlyList{ConstraintPolyline}, out string?)"/>
    /// grades them whole. A surface's window is the faces within its reach of its footprint: its max distance, or,
    /// with none, how far its batter can run before it meets the terrain. Only a window is re-triangulated, so the
    /// rest of the terrain passes through untouched: graded whole, a 3 m stair re-triangulated the whole site and
    /// dropped hundreds of points along its border (copied case 2026-10-26). A window unchanged since
    /// <paramref name="previous"/> is reused, exactly. Falls back to grading the whole terrain when a window would not
    /// weld, or when no face is within any surface's reach.
    /// </summary>
    public static GradingResult? GradeWindowed(
        IndexedTriMesh terrain,
        IReadOnlyList<SurfaceDefinition> surfaces,
        IReadOnlyList<ConstraintPolyline> barrierConstraints,
        GradingWindows.Memo? previous,
        GradingWindows.Memo next,
        List<string> notes,
        out string? errorMessage)
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = terrain;
        barrierConstraints ??= Array.Empty<ConstraintPolyline>();
        return GradeWindowedCore(vertices, vertexCount, faces, faceCount, surfaces, barrierConstraints, previous, next, notes, out errorMessage)
            ?.MeasuredAgainst(terrain, GradingTolerances.DefaultModelTolerance);
    }

    private static GradingResult? GradeWindowedCore(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<SurfaceDefinition> surfaces,
        IReadOnlyList<ConstraintPolyline> barrierConstraints,
        GradingWindows.Memo? previous,
        GradingWindows.Memo next,
        List<string> notes,
        out string? errorMessage)
    {
        var reach = new List<GradingWindows.Reach>(surfaces.Count);
        foreach (SurfaceDefinition surface in surfaces)
        {
            (double minX, double minY, double maxX, double maxY, double lowZ, double highZ) = BoundaryExtent(surface);
            double distance = surface.MaxDistance > 0
                ? surface.MaxDistance
                : GradingWindows.DaylightReach(vertices, vertexCount, (minX, minY, maxX, maxY), lowZ, highZ, surface.SlopeAngleDeg);
            reach.Add(new GradingWindows.Reach(surface.FootprintXy, surface.FootprintVertexCount, Closed: true, Filled: true, Radius: distance));
        }

        // Beyond a surface's reach, room for the faces along the window edge.
        List<GradingWindows.Reach> grown = GradingWindows.WithMargins(
            vertices, faces, faceCount, reach, GradingTolerances.DefaultModelTolerance * 100.0);

        GradingWindows.Outcome outcome = GradingWindows.Grade(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), grown, margin: 0.0, (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double MinX, double MinY, double MaxX, double MaxY) box,
                out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureStructured) =>
            {
                failurePolylines = Array.Empty<OutputPolyline>();
                failureStructured = Array.Empty<GradingDiagnostic>();
                var windowBarriers = barrierConstraints.Where(c => GradingWindows.PointsOverlap(c.Points, c.PointCount, box)).ToList();
                return GradeCore(wv, wvc, wf, wfc, items.Select(i => surfaces[i]).ToArray(), windowBarriers, out error, profile: null);
            }, (items, minX, minY, maxX, maxY, low, high) =>
            {
                var box = (minX, minY, maxX, maxY);
                void Add(double value)
                {
                    low.Add(value);
                    high.Add(value);
                }

                foreach (int i in items)
                {
                    SurfaceDefinition surface = surfaces[i];
                    Add(surface.FootprintVertexCount);
                    for (int k = 0; k < surface.FootprintVertexCount * 2; k++)
                        Add(surface.FootprintXy[k]);
                    Add(surface.BoundaryVertexCount);
                    for (int k = 0; k < surface.BoundaryVertexCount * 3; k++)
                        Add(surface.BoundaryVertices[k]);
                    Add(surface.PlaneXCoeff); Add(surface.PlaneYCoeff); Add(surface.PlaneConstant);
                    Add(surface.SlopeAngleDeg); Add(surface.MaxDistance);
                }

                foreach (ConstraintPolyline c in barrierConstraints.Where(c => GradingWindows.PointsOverlap(c.Points, c.PointCount, box)))
                {
                    Add(c.PointCount);
                    Add(c.IsClosed ? 1 : 0);
                    Add(c.PreserveInputElevation ? 1 : 0);
                    for (int k = 0; k < c.PointCount * 3; k++)
                        Add(c.Points[k]);
                }
            }, previous, next);

        if (outcome.NeedsWholeMesh || outcome.WindowCount == 0)
        {
            notes.Add(outcome.NeedsWholeMesh
                ? "In-Situ Stair graded the whole terrain: a surface's window changed a face it shares with the rest."
                : "In-Situ Stair graded the whole terrain: no face is within a surface's reach.");
            return GradeCore(vertices, vertexCount, faces, faceCount, surfaces, barrierConstraints, out errorMessage, profile: null);
        }

        notes.Add($"In-Situ Stair graded {outcome.WindowCount:N0} window(s); {outcome.ReusedWindows:N0} unchanged since the last build ({outcome.Timings}).");
        errorMessage = outcome.Errors.Count > 0 ? string.Join(" ", outcome.Errors) : null;
        return outcome.Result;
    }
}
