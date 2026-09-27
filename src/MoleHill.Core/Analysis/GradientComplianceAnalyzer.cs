using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Analysis;

/// <summary>
/// Checks the built surface against accessibility gradient limits.
/// </summary>
/// <remarks>
/// <para>This is not a thresholded slope map, and the difference is the reason it exists. A slope map
/// measures each face's steepest fall. Accessibility rules are measured the way a surveyor measures them,
/// with a level over a length. So a face's value here is the gradient <em>averaged over a footprint</em>
/// (<see cref="Options.MeasurementLength"/>), not the face's own. On a TIN a single sliver triangle can
/// read 6% on a landing a level would call 1.5%, and a check that fails on that gets switched off.</para>
///
/// <para>The average is the plan-area-weighted mean of the face gradient vectors within the footprint.
/// For a plane that is exact, and for a surface it is the plane a least-squares fit over the footprint
/// would find, which is what a level laid across it reads.</para>
///
/// <para>Level areas (landings, turning spaces, plazas) have no direction of travel, so their rule is a
/// limit in every direction: the averaged gradient's magnitude. Only faces inside the areas are measured,
/// and only inside faces are averaged. A landing checked at its edge must not borrow slope from the ramp
/// beside it.</para>
/// </remarks>
public static class GradientComplianceAnalyzer
{
    /// <summary>What the check concluded about one face.</summary>
    public enum FaceVerdict : byte
    {
        /// <summary>Outside every level area: no rule applies.</summary>
        Unchecked = 0,

        /// <summary>Inside a level area, within the limit.</summary>
        Pass = 1,

        /// <summary>Inside a level area, steeper than the limit.</summary>
        Exceeds = 2,
    }

    public sealed class Options
    {
        /// <summary>The steepest gradient a level area may have in any direction, as rise over run.</summary>
        public double LevelAreaMaxSlopeRatio { get; init; }

        /// <summary>
        /// The length the gradient is averaged over, in model units, used as a footprint diameter. Zero or
        /// less measures each face alone.
        /// </summary>
        public double MeasurementLength { get; init; }

        public Func<bool>? CancellationRequested { get; init; }
    }

    public sealed class Result
    {
        /// <summary>Per face: whether it is checked, and if so whether it passes.</summary>
        public required FaceVerdict[] Verdicts { get; init; }

        /// <summary>Per face: the averaged gradient as rise over run. NaN where the face is unchecked.</summary>
        public required double[] MeasuredSlopeRatios { get; init; }

        /// <summary>Plan area of every level area face combined.</summary>
        public double CheckedArea { get; init; }

        /// <summary>Plan area of the faces steeper than the limit.</summary>
        public double ExceedingArea { get; init; }

        /// <summary>The steepest averaged gradient found in any level area, or null when none was checked.</summary>
        public double? MaxSlopeRatio { get; init; }

        public static Result Empty(int faceCount)
        {
            var slopes = new double[faceCount];
            Array.Fill(slopes, double.NaN);
            return new Result { Verdicts = new FaceVerdict[faceCount], MeasuredSlopeRatios = slopes };
        }
    }

    /// <summary>
    /// Rounding allowance on the limit. A landing graded at exactly 1:48 must pass, and the face normals
    /// that measure it are computed in floating point.
    /// </summary>
    private const double LimitTolerance = 1e-9;

    /// <summary>A face with less plan area than this is standing on end and has no gradient worth averaging.</summary>
    private const double MinimumPlanAreaShare = 1e-12;

    /// <summary>
    /// Checks every face whose centroid lies inside the level areas against
    /// <see cref="Options.LevelAreaMaxSlopeRatio"/>.
    /// </summary>
    /// <param name="levelAreaLoops">
    /// Closed boundaries as flat XY arrays (<c>[x0, y0, x1, y1, …]</c>, not repeating the first point).
    /// Membership is even-odd across all loops together, so a loop inside another is a hole, the same
    /// reading Project To's boundaries use.
    /// </param>
    public static Result EvaluateLevelAreas(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<double[]> levelAreaLoops,
        Options options)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(levelAreaLoops);
        ArgumentNullException.ThrowIfNull(options);
        if (faceCount < 0 || faces.Length < faceCount * 3)
            throw new ArgumentException("Face array is shorter than the face count.", nameof(faces));
        if (vertexCount < 0 || vertices.Length < vertexCount * 3)
            throw new ArgumentException("Vertex array is shorter than the vertex count.", nameof(vertices));

        Result empty = Result.Empty(faceCount);
        if (faceCount == 0 || levelAreaLoops.Count == 0)
            return empty;

        var centroidX = new double[faceCount];
        var centroidY = new double[faceCount];
        var gradientX = new double[faceCount];
        var gradientY = new double[faceCount];
        var planAreas = new double[faceCount];
        var inside = new bool[faceCount];
        int insideCount = 0;
        double totalPlanArea = 0.0;

        for (int face = 0; face < faceCount; face++)
        {
            if ((face & 0x3FFF) == 0 && options.CancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();

            MeasureFace(vertices, faces, face, out centroidX[face], out centroidY[face],
                out gradientX[face], out gradientY[face], out planAreas[face]);
            totalPlanArea += planAreas[face];

            if (IsInsideLevelAreas(centroidX[face], centroidY[face], levelAreaLoops))
            {
                inside[face] = true;
                insideCount++;
            }
        }

        if (insideCount == 0)
            return empty;

        double minimumPlanArea = totalPlanArea * MinimumPlanAreaShare;
        double radius = Math.Max(0.0, options.MeasurementLength) * 0.5;
        double limit = Math.Max(0.0, options.LevelAreaMaxSlopeRatio);

        int[] insideFaces = new int[insideCount];
        for (int face = 0, next = 0; face < faceCount; face++)
        {
            if (inside[face])
                insideFaces[next++] = face;
        }

        SpatialHashGrid2D? grid = null;
        if (radius > 0.0)
        {
            var bounds = new Bounds2D[insideCount];
            for (int i = 0; i < insideCount; i++)
            {
                int face = insideFaces[i];
                bounds[i] = Bounds2D.FromPoint(centroidX[face], centroidY[face]);
            }

            grid = SpatialHashGrid2D.Build(bounds);
        }

        var verdicts = empty.Verdicts;
        var slopes = empty.MeasuredSlopeRatios;
        var candidates = new List<int>();
        var scratch = new SpatialHashGrid2D.QueryScratch(insideCount);
        double radiusSquared = radius * radius;
        double checkedArea = 0.0;
        double exceedingArea = 0.0;
        double maxSlope = double.NegativeInfinity;

        for (int i = 0; i < insideCount; i++)
        {
            if ((i & 0x3FFF) == 0 && options.CancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();

            int face = insideFaces[i];
            double sumX;
            double sumY;
            double sumArea;

            if (grid == null)
            {
                sumX = gradientX[face] * planAreas[face];
                sumY = gradientY[face] * planAreas[face];
                sumArea = planAreas[face];
            }
            else
            {
                sumX = 0.0;
                sumY = 0.0;
                sumArea = 0.0;
                double cx = centroidX[face];
                double cy = centroidY[face];
                candidates.Clear();
                grid.GatherCandidates(
                    new Bounds2D(cx - radius, cx + radius, cy - radius, cy + radius), candidates, scratch);

                foreach (int candidate in candidates)
                {
                    int other = insideFaces[candidate];
                    double dx = centroidX[other] - cx;
                    double dy = centroidY[other] - cy;
                    if ((dx * dx) + (dy * dy) > radiusSquared || planAreas[other] <= minimumPlanArea)
                        continue;

                    sumX += gradientX[other] * planAreas[other];
                    sumY += gradientY[other] * planAreas[other];
                    sumArea += planAreas[other];
                }
            }

            // A vertical face inside a level area has no gradient to measure. It is still part of the
            // area, but a wall standing in a landing is a different rule's problem.
            if (sumArea <= minimumPlanArea)
                continue;

            double slope = Math.Sqrt((sumX * sumX) + (sumY * sumY)) / sumArea;
            slopes[face] = slope;
            bool exceeds = slope > limit + LimitTolerance;
            verdicts[face] = exceeds ? FaceVerdict.Exceeds : FaceVerdict.Pass;
            checkedArea += planAreas[face];
            if (exceeds)
                exceedingArea += planAreas[face];
            if (slope > maxSlope)
                maxSlope = slope;
        }

        return new Result
        {
            Verdicts = verdicts,
            MeasuredSlopeRatios = slopes,
            CheckedArea = checkedArea,
            ExceedingArea = exceedingArea,
            MaxSlopeRatio = double.IsFinite(maxSlope) ? maxSlope : null,
        };
    }

    private static bool IsInsideLevelAreas(double x, double y, IReadOnlyList<double[]> loops)
    {
        bool inside = false;
        foreach (double[] loop in loops)
        {
            int count = loop.Length / 2;
            if (count >= 3 && PadGrader.PointInPolygon(x, y, loop, count))
                inside = !inside;
        }

        return inside;
    }

    /// <summary>
    /// A face's plan centroid, its gradient (dz/dx, dz/dy) and its plan area. The gradient comes from the
    /// face plane: for normal (nx, ny, nz) the plane z = … has dz/dx = -nx/nz and dz/dy = -ny/nz. A face
    /// with no plan extent reports zero area and zero gradient, so it carries no weight in any average.
    /// </summary>
    private static void MeasureFace(
        double[] vertices,
        int[] faces,
        int face,
        out double centroidX,
        out double centroidY,
        out double gradientX,
        out double gradientY,
        out double planArea)
    {
        int a = faces[face * 3] * 3;
        int b = faces[(face * 3) + 1] * 3;
        int c = faces[(face * 3) + 2] * 3;

        centroidX = (vertices[a] + vertices[b] + vertices[c]) / 3.0;
        centroidY = (vertices[a + 1] + vertices[b + 1] + vertices[c + 1]) / 3.0;

        double e1x = vertices[b] - vertices[a];
        double e1y = vertices[b + 1] - vertices[a + 1];
        double e1z = vertices[b + 2] - vertices[a + 2];
        double e2x = vertices[c] - vertices[a];
        double e2y = vertices[c + 1] - vertices[a + 1];
        double e2z = vertices[c + 2] - vertices[a + 2];
        double nx = (e1y * e2z) - (e1z * e2y);
        double ny = (e1z * e2x) - (e1x * e2z);
        double nz = (e1x * e2y) - (e1y * e2x);

        planArea = Math.Abs(nz) * 0.5;
        if (nz == 0.0)
        {
            gradientX = 0.0;
            gradientY = 0.0;
            return;
        }

        gradientX = -nx / nz;
        gradientY = -ny / nz;
    }
}
