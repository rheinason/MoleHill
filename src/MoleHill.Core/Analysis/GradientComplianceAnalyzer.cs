using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Geometry;

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

    /// <summary>What the route check concluded about one face.</summary>
    public enum RouteVerdict : byte
    {
        /// <summary>Not within any route corridor.</summary>
        Unchecked = 0,

        /// <summary>Running slope within the walk limit, cross slope within its limit.</summary>
        Walk = 1,

        /// <summary>Running slope past the walk limit but within the ramp limit: allowed, as a ramp.</summary>
        Ramp = 2,

        /// <summary>Running slope steeper than the ramp limit.</summary>
        RunningExceeds = 3,

        /// <summary>Running slope acceptable, cross slope steeper than its limit.</summary>
        CrossExceeds = 4,
    }

    public sealed class RouteOptions
    {
        /// <summary>The steepest running slope that is still a walk, as rise over run.</summary>
        public double WalkMaxRunningRatio { get; init; }

        /// <summary>
        /// The steepest running slope allowed at all, as a ramp. At or below the walk limit there is no
        /// ramp band. Positive infinity switches the running check off.
        /// </summary>
        public double RampMaxRunningRatio { get; init; }

        /// <summary>The steepest cross slope. Positive infinity switches the check off.</summary>
        public double MaxCrossRatio { get; init; } = double.PositiveInfinity;

        /// <summary>Corridor width in model units, centred on the route.</summary>
        public double Width { get; init; }

        /// <summary>Footprint diameter the gradient is averaged over. See <see cref="Options.MeasurementLength"/>.</summary>
        public double MeasurementLength { get; init; }

        public Func<bool>? CancellationRequested { get; init; }
    }

    public sealed class RouteResult
    {
        public required RouteVerdict[] Verdicts { get; init; }

        /// <summary>Per face: averaged slope along the route, as a ratio. NaN where unchecked.</summary>
        public required double[] RunningRatios { get; init; }

        /// <summary>Per face: averaged slope across the route, as a ratio. NaN where unchecked.</summary>
        public required double[] CrossRatios { get; init; }

        public double CheckedArea { get; init; }

        /// <summary>Plan area in the ramp band: allowed, and what stage three will hold to ramp rules.</summary>
        public double RampArea { get; init; }

        public double RunningExceedingArea { get; init; }

        public double CrossExceedingArea { get; init; }

        public double? MaxRunningRatio { get; init; }

        public double? MaxCrossRatio { get; init; }

        public static RouteResult Empty(int faceCount)
        {
            var running = new double[faceCount];
            var cross = new double[faceCount];
            Array.Fill(running, double.NaN);
            Array.Fill(cross, double.NaN);
            return new RouteResult { Verdicts = new RouteVerdict[faceCount], RunningRatios = running, CrossRatios = cross };
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

        FaceField field = MeasureFaces(vertices, faces, faceCount, options.CancellationRequested);
        var inside = new bool[faceCount];
        int insideCount = 0;
        for (int face = 0; face < faceCount; face++)
        {
            if (IsInsideLevelAreas(field.CentroidX[face], field.CentroidY[face], levelAreaLoops))
            {
                inside[face] = true;
                insideCount++;
            }
        }

        if (insideCount == 0)
            return empty;

        double limit = Math.Max(0.0, options.LevelAreaMaxSlopeRatio);
        var averager = new GradientAverager(field, inside, options.MeasurementLength);
        var verdicts = empty.Verdicts;
        var slopes = empty.MeasuredSlopeRatios;
        double checkedArea = 0.0;
        double exceedingArea = 0.0;
        double maxSlope = double.NegativeInfinity;

        for (int face = 0; face < faceCount; face++)
        {
            if ((face & 0x3FFF) == 0 && options.CancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();

            // A vertical face inside a level area has no gradient to measure. It is still part of the
            // area, but a wall standing in a landing is a different rule's problem.
            if (!inside[face] || !averager.TryAverage(face, out double gx, out double gy))
                continue;

            double slope = Math.Sqrt((gx * gx) + (gy * gy));
            slopes[face] = slope;
            bool exceeds = slope > limit + LimitTolerance;
            verdicts[face] = exceeds ? FaceVerdict.Exceeds : FaceVerdict.Pass;
            checkedArea += field.PlanAreas[face];
            if (exceeds)
                exceedingArea += field.PlanAreas[face];
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

    /// <summary>
    /// Checks every face within half of <see cref="RouteOptions.Width"/> of a route against running and
    /// cross slope limits.
    /// </summary>
    /// <remarks>
    /// <para>The averaged gradient is split against the nearest route segment's plan direction: the
    /// component along it is the running slope, the component across it the cross slope. Both are
    /// magnitudes, so a route checks the same whichever way it was drawn.</para>
    ///
    /// <para>Running slope takes the gentlest band it fits: walk, then ramp. Steeper than the ramp limit
    /// exceeds. Run length, rise between landings and the landings themselves are stage three: this
    /// classifies each piece of ground and does not yet follow the route along its length.</para>
    ///
    /// <para>Averaging only uses faces inside some corridor, for the reason level areas only use their
    /// own: a walk measured at its edge must not borrow slope from the bank beside it.</para>
    /// </remarks>
    /// <param name="routes">Route centrelines as flat XY arrays (<c>[x0, y0, x1, y1, …]</c>).</param>
    public static RouteResult EvaluateRoutes(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<double[]> routes,
        RouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(options);
        if (faceCount < 0 || faces.Length < faceCount * 3)
            throw new ArgumentException("Face array is shorter than the face count.", nameof(faces));
        if (vertexCount < 0 || vertices.Length < vertexCount * 3)
            throw new ArgumentException("Vertex array is shorter than the vertex count.", nameof(vertices));

        RouteResult result = RouteResult.Empty(faceCount);
        double halfWidth = Math.Max(0.0, options.Width) * 0.5;
        if (faceCount == 0 || routes.Count == 0 || halfWidth <= 0.0)
            return result;

        var segments = new List<(double X0, double Y0, double X1, double Y1)>();
        foreach (double[] route in routes)
        {
            for (int i = 0; i + 3 < route.Length; i += 2)
            {
                if (route[i] != route[i + 2] || route[i + 1] != route[i + 3])
                    segments.Add((route[i], route[i + 1], route[i + 2], route[i + 3]));
            }
        }

        if (segments.Count == 0)
            return result;

        var segmentBounds = new Bounds2D[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            var (x0, y0, x1, y1) = segments[i];
            segmentBounds[i] = new Bounds2D(
                Math.Min(x0, x1) - halfWidth, Math.Max(x0, x1) + halfWidth,
                Math.Min(y0, y1) - halfWidth, Math.Max(y0, y1) + halfWidth);
        }

        SpatialHashGrid2D segmentGrid = SpatialHashGrid2D.Build(segmentBounds);
        FaceField field = MeasureFaces(vertices, faces, faceCount, options.CancellationRequested);
        var member = new bool[faceCount];
        var tangentX = new double[faceCount];
        var tangentY = new double[faceCount];
        var candidates = new List<int>();
        var scratch = new SpatialHashGrid2D.QueryScratch(segments.Count);
        int memberCount = 0;

        for (int face = 0; face < faceCount; face++)
        {
            if ((face & 0x3FFF) == 0 && options.CancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();

            double cx = field.CentroidX[face];
            double cy = field.CentroidY[face];
            segmentGrid.GatherCandidates(Bounds2D.FromPoint(cx, cy), candidates, scratch);
            double best = double.PositiveInfinity;
            foreach (int candidate in candidates)
            {
                var (x0, y0, x1, y1) = segments[candidate];
                double dx = x1 - x0;
                double dy = y1 - y0;
                double lengthSquared = (dx * dx) + (dy * dy);
                double t = Math.Clamp((((cx - x0) * dx) + ((cy - y0) * dy)) / lengthSquared, 0.0, 1.0);
                double ex = x0 + (t * dx) - cx;
                double ey = y0 + (t * dy) - cy;
                double distanceSquared = (ex * ex) + (ey * ey);
                if (distanceSquared >= best)
                    continue;

                best = distanceSquared;
                double length = Math.Sqrt(lengthSquared);
                tangentX[face] = dx / length;
                tangentY[face] = dy / length;
            }

            if (best <= halfWidth * halfWidth)
            {
                member[face] = true;
                memberCount++;
            }
        }

        if (memberCount == 0)
            return result;

        var averager = new GradientAverager(field, member, options.MeasurementLength);
        double walkLimit = options.WalkMaxRunningRatio + LimitTolerance;
        double rampLimit = Math.Max(options.WalkMaxRunningRatio, options.RampMaxRunningRatio) + LimitTolerance;
        double crossLimit = options.MaxCrossRatio + LimitTolerance;
        double checkedArea = 0.0;
        double rampArea = 0.0;
        double runningExceedingArea = 0.0;
        double crossExceedingArea = 0.0;
        double maxRunning = double.NegativeInfinity;
        double maxCross = double.NegativeInfinity;

        for (int face = 0; face < faceCount; face++)
        {
            if ((face & 0x3FFF) == 0 && options.CancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();

            if (!member[face] || !averager.TryAverage(face, out double gx, out double gy))
                continue;

            double running = Math.Abs((gx * tangentX[face]) + (gy * tangentY[face]));
            double cross = Math.Abs((gy * tangentX[face]) - (gx * tangentY[face]));
            result.RunningRatios[face] = running;
            result.CrossRatios[face] = cross;

            double area = field.PlanAreas[face];
            checkedArea += area;
            maxRunning = Math.Max(maxRunning, running);
            maxCross = Math.Max(maxCross, cross);

            bool runningExceeds = running > rampLimit;
            bool crossExceeds = cross > crossLimit;
            if (runningExceeds)
                runningExceedingArea += area;
            if (crossExceeds)
                crossExceedingArea += area;

            // Running slope is named first when both fail: it decides whether the route is usable at all,
            // where a cross-slope failure is usually fixed by regrading across it.
            RouteVerdict verdict = runningExceeds ? RouteVerdict.RunningExceeds
                : crossExceeds ? RouteVerdict.CrossExceeds
                : running > walkLimit ? RouteVerdict.Ramp
                : RouteVerdict.Walk;
            if (verdict == RouteVerdict.Ramp)
                rampArea += area;
            result.Verdicts[face] = verdict;
        }

        return new RouteResult
        {
            Verdicts = result.Verdicts,
            RunningRatios = result.RunningRatios,
            CrossRatios = result.CrossRatios,
            CheckedArea = checkedArea,
            RampArea = rampArea,
            RunningExceedingArea = runningExceedingArea,
            CrossExceedingArea = crossExceedingArea,
            MaxRunningRatio = double.IsFinite(maxRunning) ? maxRunning : null,
            MaxCrossRatio = double.IsFinite(maxCross) ? maxCross : null,
        };
    }

    /// <summary>Per-face plan centroid, gradient and plan area, measured once per check.</summary>
    private sealed class FaceField
    {
        public required double[] CentroidX { get; init; }
        public required double[] CentroidY { get; init; }
        public required double[] GradientX { get; init; }
        public required double[] GradientY { get; init; }
        public required double[] PlanAreas { get; init; }

        /// <summary>Faces with less plan area than this are standing on end and carry no gradient.</summary>
        public required double MinimumPlanArea { get; init; }
    }

    private static FaceField MeasureFaces(double[] vertices, int[] faces, int faceCount, Func<bool>? cancellationRequested)
    {
        var centroidX = new double[faceCount];
        var centroidY = new double[faceCount];
        var gradientX = new double[faceCount];
        var gradientY = new double[faceCount];
        var planAreas = new double[faceCount];
        double totalPlanArea = 0.0;
        for (int face = 0; face < faceCount; face++)
        {
            if ((face & 0x3FFF) == 0 && cancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();

            MeasureFace(vertices, faces, face, out centroidX[face], out centroidY[face],
                out gradientX[face], out gradientY[face], out planAreas[face]);
            totalPlanArea += planAreas[face];
        }

        return new FaceField
        {
            CentroidX = centroidX,
            CentroidY = centroidY,
            GradientX = gradientX,
            GradientY = gradientY,
            PlanAreas = planAreas,
            MinimumPlanArea = totalPlanArea * MinimumPlanAreaShare,
        };
    }

    /// <summary>
    /// The plan-area-weighted mean gradient over a footprint, drawn only from member faces. A zero
    /// measurement length measures each face alone.
    /// </summary>
    private sealed class GradientAverager
    {
        private readonly FaceField _field;
        private readonly int[] _members;
        private readonly SpatialHashGrid2D? _grid;
        private readonly double _radius;
        private readonly List<int> _candidates = new();
        private readonly SpatialHashGrid2D.QueryScratch? _scratch;

        public GradientAverager(FaceField field, bool[] member, double measurementLength)
        {
            _field = field;
            _radius = Math.Max(0.0, measurementLength) * 0.5;
            var members = new List<int>();
            for (int face = 0; face < member.Length; face++)
            {
                if (member[face])
                    members.Add(face);
            }

            _members = members.ToArray();
            if (_radius <= 0.0 || _members.Length == 0)
                return;

            var bounds = new Bounds2D[_members.Length];
            for (int i = 0; i < _members.Length; i++)
                bounds[i] = Bounds2D.FromPoint(field.CentroidX[_members[i]], field.CentroidY[_members[i]]);
            _grid = SpatialHashGrid2D.Build(bounds);
            _scratch = new SpatialHashGrid2D.QueryScratch(_members.Length);
        }

        public bool TryAverage(int face, out double gradientX, out double gradientY)
        {
            FaceField f = _field;
            double sumX = 0.0;
            double sumY = 0.0;
            double sumArea = 0.0;
            if (_grid == null)
            {
                sumX = f.GradientX[face] * f.PlanAreas[face];
                sumY = f.GradientY[face] * f.PlanAreas[face];
                sumArea = f.PlanAreas[face];
            }
            else
            {
                double cx = f.CentroidX[face];
                double cy = f.CentroidY[face];
                double radiusSquared = _radius * _radius;
                _grid.GatherCandidates(
                    new Bounds2D(cx - _radius, cx + _radius, cy - _radius, cy + _radius), _candidates, _scratch);
                foreach (int candidate in _candidates)
                {
                    int other = _members[candidate];
                    double dx = f.CentroidX[other] - cx;
                    double dy = f.CentroidY[other] - cy;
                    if ((dx * dx) + (dy * dy) > radiusSquared || f.PlanAreas[other] <= f.MinimumPlanArea)
                        continue;

                    sumX += f.GradientX[other] * f.PlanAreas[other];
                    sumY += f.GradientY[other] * f.PlanAreas[other];
                    sumArea += f.PlanAreas[other];
                }
            }

            if (sumArea <= f.MinimumPlanArea)
            {
                gradientX = 0.0;
                gradientY = 0.0;
                return false;
            }

            gradientX = sumX / sumArea;
            gradientY = sumY / sumArea;
            return true;
        }
    }

    private static bool IsInsideLevelAreas(double x, double y, IReadOnlyList<double[]> loops)
    {
        bool inside = false;
        foreach (double[] loop in loops)
        {
            int count = loop.Length / 2;
            if (count >= 3 && Geometry2D.PointInPolygon(x, y, loop, count))
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
