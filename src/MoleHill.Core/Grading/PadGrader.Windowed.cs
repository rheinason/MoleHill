using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    /// <summary>
    /// Grades the pads window by window (<see cref="GradingWindows"/>), as
    /// <see cref="Grade(double[], int, int[], int, PadBoundary[], LockCurve[], out string, out IReadOnlyList{OutputPolyline}, out IReadOnlyList{GradingDiagnostic}, double, double, IReadOnlyList{SurfaceRemesher.ConstraintPolyline})"/>
    /// grades them whole: each group of pads whose reach overlaps is graded on the
    /// faces under its window, and a window unchanged since <paramref name="previous"/> is reused, exactly.
    /// A pad's reach is its outline grown by its max distance, or, with none, by how far its batter can run
    /// before it meets the terrain. Falls back to grading the whole terrain when a window would not weld.
    /// </summary>
    public static GradingResult? GradeWindowed(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[] locks,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance,
        double terrainDetailSize,
        GradingWindows.Memo? previous,
        GradingWindows.Memo next,
        List<string> notes,
        out string? errorMessage,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        out IReadOnlyList<GradingDiagnostic> failureDiagnostics)
    {
        var reach = new List<(double MinX, double MinY, double MaxX, double MaxY)>(pads.Length);
        foreach (PadBoundary pad in pads)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            double lowZ = double.MaxValue, highZ = double.MinValue;
            for (int i = 0; i < pad.VertexCount; i++)
            {
                double x = pad.BoundaryVertices[i * 3], y = pad.BoundaryVertices[i * 3 + 1], z = pad.BoundaryVertices[i * 3 + 2];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                lowZ = Math.Min(lowZ, z); highZ = Math.Max(highZ, z);
            }

            double distance = pad.MaxDistance > 0
                ? pad.MaxDistance
                : GradingWindows.DaylightReach(vertices, vertexCount, (minX, minY, maxX, maxY), lowZ, highZ, Math.Min(pad.SlopeAngleDeg, pad.FillSlopeAngleDeg));
            double grow = distance + pad.StitchApronDistance;
            reach.Add((minX - grow, minY - grow, maxX + grow, maxY + grow));
        }

        // Beyond an item's reach, room for the faces along the window edge: twice the longest face near it.
        double margin = Math.Max(modelTolerance * 100.0, 2.0 * GradingWindows.LongestPlanEdgeNear(vertices, faces, faceCount, reach));

        GradingWindows.Outcome outcome = GradingWindows.Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            reach,
            margin,
            (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double MinX, double MinY, double MaxX, double MaxY) box,
                out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureStructured) =>
            {
                LockCurve[] windowLocks = locks.Where(l => GradingWindows.XyOverlaps(l.XyVertices, l.VertexCount, box)).ToArray();
                var windowConstraints = hardConstraints.Where(c => GradingWindows.PointsOverlap(c.Points, c.PointCount, box)).ToList();
                return Grade(
                    wv, wvc, wf, wfc,
                    items.Select(i => pads[i]).ToArray(),
                    windowLocks.Length > 0 ? windowLocks : null,
                    out error,
                    out failurePolylines,
                    out failureStructured,
                    modelTolerance,
                    terrainDetailSize,
                    windowConstraints);
            },
            (items, minX, minY, maxX, maxY, low, high) =>
            {
                var box = (minX, minY, maxX, maxY);
                void Add(double value)
                {
                    low.Add(value);
                    high.Add(value);
                }

                Add(modelTolerance);
                Add(terrainDetailSize);
                foreach (int i in items)
                {
                    PadBoundary pad = pads[i];
                    foreach (double d in pad.BoundaryVertices)
                        Add(d);
                    Add(pad.PlaneXCoeff); Add(pad.PlaneYCoeff); Add(pad.PlaneConstant);
                    Add(pad.SlopeAngleDeg); Add(pad.FillSlopeAngleDeg); Add(pad.MaxDistance);
                    Add(pad.CornerFanSegments); Add(pad.StitchApronDistance);
                }

                foreach (LockCurve l in locks.Where(l => GradingWindows.XyOverlaps(l.XyVertices, l.VertexCount, box)))
                {
                    Add(l.VertexCount);
                    for (int k = 0; k < l.VertexCount * 2; k++)
                        Add(l.XyVertices[k]);
                }

                foreach (SurfaceRemesher.ConstraintPolyline c in hardConstraints.Where(c => GradingWindows.PointsOverlap(c.Points, c.PointCount, box)))
                {
                    Add(c.PointCount);
                    Add(c.IsClosed ? 1 : 0);
                    Add(c.PreserveInputElevation ? 1 : 0);
                    for (int k = 0; k < c.PointCount * 3; k++)
                        Add(c.Points[k]);
                }
            },
            previous,
            next);

        if (outcome.NeedsWholeMesh)
        {
            notes.Add("Grade Pad graded the whole terrain: a pad's window changed a face it shares with the rest.");
            return Grade(
                vertices, vertexCount, faces, faceCount, pads, locks.Length > 0 ? locks : null,
                out errorMessage, out failureOutputPolylines, out failureDiagnostics, modelTolerance, terrainDetailSize, hardConstraints);
        }

        notes.Add($"Grade Pad graded {outcome.WindowCount:N0} window(s); {outcome.ReusedWindows:N0} unchanged since the last build.");
        errorMessage = outcome.Errors.Count > 0 ? string.Join(" ", outcome.Errors) : null;
        failureOutputPolylines = outcome.FailureOutputPolylines;
        failureDiagnostics = outcome.FailureDiagnostics;
        return outcome.Result;
    }
}
