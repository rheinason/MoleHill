using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Grades the paths window by window (<see cref="GradingWindows"/>), as
    /// <see cref="Grade(double[], int, int[], int, PathDefinition[], IReadOnlyList{ConstraintPolyline}, out string?, double, bool)"/>
    /// grades them whole. A path's window is the band of faces within its reach of its centreline: its widest
    /// half-width plus its max distance, or, with none, how far its batter can run before it meets the terrain.
    /// Connected paths share one window, but the land between them is not in it, so an edit there reuses it.
    /// Falls back to grading the whole terrain when a window would not weld.
    /// </summary>
    /// <summary>
    /// <see cref="Grade(PathGradeRequest)"/> restricted to the windows that changed since
    /// <paramref name="previous"/>; <paramref name="next"/> records this run for the following one.
    /// </summary>
    public static GradeOutcome GradeWindowed(
        PathGradeRequest request,
        GradingWindows.Memo? previous,
        GradingWindows.Memo next,
        List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(request);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = request.Terrain;
        GradingResult? result = GradeWindowed(
            vertices, vertexCount, faces, faceCount, request.Paths, request.HardConstraints,
            request.ModelTolerance, request.PreferSplitKeep, previous, next, notes,
            out string? errorMessage);
        return new GradeOutcome { Result = result, ErrorMessage = errorMessage };
    }

    private static GradingResult? GradeWindowed(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double modelTolerance,
        bool preferSplitKeep,
        GradingWindows.Memo? previous,
        GradingWindows.Memo next,
        List<string> notes,
        out string? errorMessage)
    {
        var reach = new List<GradingWindows.Reach>(paths.Length);
        foreach (PathDefinition path in paths)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            double lowZ = double.MaxValue, highZ = double.MinValue;
            for (int i = 0; i < path.VertexCount; i++)
            {
                minX = Math.Min(minX, path.XyVertices[i * 2]); maxX = Math.Max(maxX, path.XyVertices[i * 2]);
                minY = Math.Min(minY, path.XyVertices[i * 2 + 1]); maxY = Math.Max(maxY, path.XyVertices[i * 2 + 1]);
                lowZ = Math.Min(lowZ, path.ZValues[i]);
                highZ = Math.Max(highZ, path.ZValues[i]);
            }

            double flattest = Math.Min(
                Math.Min(path.LeftCutSlopeAngleDeg, path.RightCutSlopeAngleDeg),
                Math.Min(path.LeftFillSlopeAngleDeg, path.RightFillSlopeAngleDeg));
            double halfWidth = path.MaximumHalfWidth();
            double distance = path.MaxDistance > 0
                ? path.MaxDistance
                : GradingWindows.DaylightReach(vertices, vertexCount, (minX - halfWidth, minY - halfWidth, maxX + halfWidth, maxY + halfWidth), lowZ, highZ, flattest);
            reach.Add(new GradingWindows.Reach(path.XyVertices, path.VertexCount, path.IsClosed, Filled: false, Radius: halfWidth + distance));
        }

        // Beyond an item's reach, room for the faces along the window edge.
        List<GradingWindows.Reach> grown = GradingWindows.WithMargins(vertices, faces, faceCount, reach, modelTolerance * 100.0);

        GradingWindows.Outcome outcome = GradingWindows.Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            grown,
            margin: 0.0,
            (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double MinX, double MinY, double MaxX, double MaxY) box,
                out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureStructured) =>
            {
                failurePolylines = Array.Empty<OutputPolyline>();
                failureStructured = Array.Empty<GradingDiagnostic>();
                var windowConstraints = hardConstraints.Where(c => GradingWindows.PointsOverlap(c.Points, c.PointCount, box)).ToList();
                return GradeCore(wv, wvc, wf, wfc, items.Select(i => paths[i]).ToArray(), windowConstraints, out error, modelTolerance, preferSplitKeep, performanceTimings: null);
            },
            (items, minX, minY, maxX, maxY, low, high) =>
            {
                var box = (minX, minY, maxX, maxY);
                void Add(double value)
                {
                    low.Add(value);
                    high.Add(value);
                }

                void AddAll(double[]? values)
                {
                    Add(values?.Length ?? -1);
                    if (values != null)
                    {
                        foreach (double d in values)
                            Add(d);
                    }
                }

                Add(modelTolerance);
                Add(preferSplitKeep ? 1 : 0);
                foreach (int i in items)
                {
                    PathDefinition path = paths[i];
                    Add(path.VertexCount);
                    AddAll(path.XyVertices);
                    AddAll(path.ZValues);
                    Add(path.Width);
                    Add(path.SlopeAngleDeg); Add(path.FillSlopeAngleDeg); Add(path.MaxDistance);
                    AddAll(path.LeftEdgeXy);
                    AddAll(path.RightEdgeXy);
                    Add(path.IsClosed ? 1 : 0);
                    Add(path.LeftCutSlopeAngleDeg); Add(path.LeftFillSlopeAngleDeg);
                    Add(path.RightCutSlopeAngleDeg); Add(path.RightFillSlopeAngleDeg);
                    AddAll(path.OutwardNormals);
                }

                foreach (ConstraintPolyline c in hardConstraints.Where(c => GradingWindows.PointsOverlap(c.Points, c.PointCount, box)))
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
            notes.Add("Grade Path graded the whole terrain: a path's window changed a face it shares with the rest.");
            return GradeCore(vertices, vertexCount, faces, faceCount, paths, hardConstraints, out errorMessage, modelTolerance, preferSplitKeep, performanceTimings: null);
        }

        notes.Add($"Grade Path graded {outcome.WindowCount:N0} window(s); {outcome.ReusedWindows:N0} unchanged since the last build ({outcome.Timings}).");
        errorMessage = outcome.Errors.Count > 0 ? string.Join(" ", outcome.Errors) : null;
        return outcome.Result;
    }
}
