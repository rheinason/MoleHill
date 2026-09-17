using MoleHill.Core.Engine;

namespace MoleHill.Core.Processing;

/// <summary>
/// Certifies the vertical difference between two piecewise-linear 2.5D triangle meshes.
/// </summary>
public static class SurfaceDeviationEvaluator
{
    public sealed class Options
    {
        /// <summary>XY tolerance used for clipping and domain comparison.</summary>
        public double NumericalTolerance { get; init; } = 1e-8;

        public Func<bool>? ShouldCancel { get; init; }
    }

    public readonly record struct Result(
        bool IsValid,
        bool HasEqualDomain,
        double MaximumDeviation,
        double WorstX,
        double WorstY,
        double ReferenceArea,
        double CandidateArea,
        double OverlapArea,
        long TrianglePairsTested,
        string? FailureReason);

    private readonly record struct Point2(double X, double Y);

    /// <summary>
    /// Compares the complete XY overlay of <paramref name="referenceFaces"/> and
    /// <paramref name="candidateFaces"/>. A coverage gap is reported through
    /// <see cref="Result.HasEqualDomain"/> and is never treated as zero error.
    /// </summary>
    public static Result Evaluate(
        double[] referenceVertices,
        int[] referenceFaces,
        double[] candidateVertices,
        int[] candidateFaces,
        Options? options = null)
    {
        options ??= new Options();
        var cancellation = new CancellationProbe(options.ShouldCancel);
        cancellation.ThrowIfCancelled();

        if (!TryValidate(referenceVertices, referenceFaces, "Reference", out string? failure) ||
            !TryValidate(candidateVertices, candidateFaces, "Candidate", out failure))
        {
            return Invalid(failure!);
        }

        double scale = CoordinateScale(referenceVertices, candidateVertices);
        double lengthTolerance = options.NumericalTolerance;
        if (!double.IsFinite(lengthTolerance) || lengthTolerance < 0.0)
            return Invalid("Numerical tolerance must be finite and nonnegative.");
        lengthTolerance = Math.Max(lengthTolerance, Math.Max(scale, 1.0) * 1e-12);

        int referenceFaceCount = referenceFaces.Length / 3;
        int candidateFaceCount = candidateFaces.Length / 3;
        var candidateBounds = new Bounds2D[candidateFaceCount];
        var candidateValid = new bool[candidateFaceCount];
        var candidateAreaSum = new CompensatedSum();
        for (int face = 0; face < candidateFaceCount; face++)
        {
            cancellation.ThrowIfCancelledOften();
            if (!TryFace(candidateVertices, candidateFaces, face, out Point2 a, out Point2 b, out Point2 c, out double area))
                return Invalid($"Candidate face {face} is degenerate in XY.");

            candidateBounds[face] = Bounds(a, b, c, lengthTolerance);
            candidateValid[face] = true;
            candidateAreaSum.Add(area);
        }

        SpatialHashGrid2D index = SpatialHashGrid2D.Build(candidateBounds, candidateValid);
        var scratch = new SpatialHashGrid2D.QueryScratch(candidateFaceCount);
        var candidates = new List<int>();
        var referenceAreaSum = new CompensatedSum();
        var overlapAreaSum = new CompensatedSum();
        double maximumDeviation = 0.0;
        double worstX = double.NaN;
        double worstY = double.NaN;
        long pairsTested = 0;
        Span<Point2> polygon = stackalloc Point2[8];

        for (int referenceFace = 0; referenceFace < referenceFaceCount; referenceFace++)
        {
            cancellation.ThrowIfCancelledOften();
            if (!TryFace(referenceVertices, referenceFaces, referenceFace, out Point2 r0, out Point2 r1, out Point2 r2, out double referenceArea))
                return Invalid($"Reference face {referenceFace} is degenerate in XY.");

            referenceAreaSum.Add(referenceArea);
            Bounds2D referenceBounds = Bounds(r0, r1, r2, lengthTolerance);
            index.GatherCandidates(referenceBounds, candidates, scratch);
            foreach (int candidateFace in candidates)
            {
                cancellation.ThrowIfCancelledOften();
                if (!candidateBounds[candidateFace].Intersects(referenceBounds))
                    continue;

                pairsTested++;
                GetFacePoints(candidateVertices, candidateFaces, candidateFace, out Point2 c0, out Point2 c1, out Point2 c2);
                int count = ClipTriangle(r0, r1, r2, c0, c1, c2, lengthTolerance, polygon);
                if (count < 3)
                    continue;

                double overlapArea = PolygonArea(polygon, count);
                double areaTolerance = lengthTolerance * lengthTolerance;
                if (overlapArea <= areaTolerance)
                    continue;

                overlapAreaSum.Add(overlapArea);
                for (int i = 0; i < count; i++)
                {
                    Point2 point = polygon[i];
                    double referenceZ = InterpolateZ(referenceVertices, referenceFaces, referenceFace, point);
                    double candidateZ = InterpolateZ(candidateVertices, candidateFaces, candidateFace, point);
                    double deviation = Math.Abs(referenceZ - candidateZ);
                    if (deviation > maximumDeviation)
                    {
                        maximumDeviation = deviation;
                        worstX = point.X;
                        worstY = point.Y;
                    }
                }
            }
        }

        double referenceAreaTotal = referenceAreaSum.Value;
        double candidateAreaTotal = candidateAreaSum.Value;
        double overlapAreaTotal = overlapAreaSum.Value;
        double domainAreaTolerance = Math.Max(
            lengthTolerance * lengthTolerance * Math.Max(1, referenceFaceCount + candidateFaceCount),
            Math.Max(referenceAreaTotal, candidateAreaTotal) * 1e-10);
        bool equalDomain =
            Math.Abs(referenceAreaTotal - overlapAreaTotal) <= domainAreaTolerance &&
            Math.Abs(candidateAreaTotal - overlapAreaTotal) <= domainAreaTolerance;

        string? coverageFailure = equalDomain
            ? null
            : $"XY domains differ (reference area {referenceAreaTotal:G17}, candidate area {candidateAreaTotal:G17}, overlap {overlapAreaTotal:G17}).";
        return new Result(
            IsValid: true,
            HasEqualDomain: equalDomain,
            MaximumDeviation: maximumDeviation,
            WorstX: worstX,
            WorstY: worstY,
            ReferenceArea: referenceAreaTotal,
            CandidateArea: candidateAreaTotal,
            OverlapArea: overlapAreaTotal,
            TrianglePairsTested: pairsTested,
            FailureReason: coverageFailure);
    }

    private static bool TryValidate(double[] vertices, int[] faces, string label, out string? failure)
    {
        if (vertices is null || faces is null)
        {
            failure = $"{label} mesh arrays cannot be null.";
            return false;
        }
        if (vertices.Length == 0 || vertices.Length % 3 != 0 || faces.Length == 0 || faces.Length % 3 != 0)
        {
            failure = $"{label} mesh must contain XYZ vertices and triangle indices.";
            return false;
        }
        for (int i = 0; i < vertices.Length; i++)
        {
            if (!double.IsFinite(vertices[i]))
            {
                failure = $"{label} vertex coordinate {i} is not finite.";
                return false;
            }
        }
        int vertexCount = vertices.Length / 3;
        for (int i = 0; i < faces.Length; i++)
        {
            if ((uint)faces[i] >= (uint)vertexCount)
            {
                failure = $"{label} face index {i} is outside the vertex array.";
                return false;
            }
        }
        failure = null;
        return true;
    }

    private static Result Invalid(string reason) => new(
        false, false, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, 0, reason);

    /// <summary>Reusable exact projected-domain index for candidate-face culling.</summary>
    internal sealed class DomainCoverageIndex
    {
        private readonly double[] _vertices;
        private readonly int[] _faces;
        private readonly Bounds2D[] _bounds;
        private readonly SpatialHashGrid2D _index;
        private readonly SpatialHashGrid2D.QueryScratch _scratch;
        private readonly List<int> _candidates = new();
        private readonly double _lengthTolerance;

        internal DomainCoverageIndex(double[] vertices, int[] faces, double numericalTolerance)
        {
            _vertices = vertices;
            _faces = faces;
            _lengthTolerance = Math.Max(
                numericalTolerance,
                Math.Max(CoordinateScale(vertices, vertices), 1.0) * 1e-12);
            int faceCount = faces.Length / 3;
            _bounds = new Bounds2D[faceCount];
            var valid = new bool[faceCount];
            for (int face = 0; face < faceCount; face++)
            {
                GetFacePoints(vertices, faces, face, out Point2 a, out Point2 b, out Point2 c);
                _bounds[face] = Bounds(a, b, c, _lengthTolerance);
                valid[face] = true;
            }
            _index = SpatialHashGrid2D.Build(_bounds, valid);
            _scratch = new SpatialHashGrid2D.QueryScratch(faceCount);
        }

        internal bool CoversTriangle(double[] candidateVertices, int a, int b, int c)
        {
            Point2 p0 = Point(candidateVertices, a);
            Point2 p1 = Point(candidateVertices, b);
            Point2 p2 = Point(candidateVertices, c);
            double candidateArea = Math.Abs(Cross(p0, p1, p2)) * 0.5;
            Bounds2D candidateBounds = Bounds(p0, p1, p2, _lengthTolerance);
            _index.GatherCandidates(candidateBounds, _candidates, _scratch);
            var overlap = new CompensatedSum();
            Span<Point2> polygon = stackalloc Point2[8];
            foreach (int face in _candidates)
            {
                if (!_bounds[face].Intersects(candidateBounds))
                    continue;
                GetFacePoints(_vertices, _faces, face, out Point2 q0, out Point2 q1, out Point2 q2);
                int count = ClipTriangle(p0, p1, p2, q0, q1, q2, _lengthTolerance, polygon);
                if (count >= 3)
                    overlap.Add(PolygonArea(polygon, count));
            }

            double areaTolerance = Math.Max(
                _lengthTolerance * _lengthTolerance * Math.Max(1, _candidates.Count),
                candidateArea * 1e-10);
            return Math.Abs(candidateArea - overlap.Value) <= areaTolerance;
        }
    }

    private static double CoordinateScale(double[] first, double[] second)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        Accumulate(first, ref minX, ref maxX, ref minY, ref maxY);
        Accumulate(second, ref minX, ref maxX, ref minY, ref maxY);
        return Math.Max(maxX - minX, maxY - minY);
    }

    private static void Accumulate(double[] vertices, ref double minX, ref double maxX, ref double minY, ref double maxY)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
    }

    private static bool TryFace(double[] vertices, int[] faces, int face, out Point2 a, out Point2 b, out Point2 c, out double area)
    {
        GetFacePoints(vertices, faces, face, out a, out b, out c);
        double doubleArea = RobustCross(a, b, c);
        area = Math.Abs(doubleArea) * 0.5;
        return doubleArea != 0.0;
    }

    /// <summary>
    /// Returns a robust orientation determinant. Model tolerance must not act as a minimum triangle
    /// altitude: valid grading fans routinely contain narrow faces, and the evaluator's contract covers
    /// near-vertical 2.5D geometry. The inexpensive determinant is conclusive almost always; only the
    /// floating-point uncertainty band falls back to Triangle.NET's adaptive exact predicate.
    /// </summary>
    private static double RobustCross(Point2 a, Point2 b, Point2 c)
    {
        double detLeft = (a.X - c.X) * (b.Y - c.Y);
        double detRight = (a.Y - c.Y) * (b.X - c.X);
        double determinant = detLeft - detRight;
        double determinantSum;
        if (detLeft > 0.0)
        {
            if (detRight <= 0.0)
                return determinant;
            determinantSum = detLeft + detRight;
        }
        else if (detLeft < 0.0)
        {
            if (detRight >= 0.0)
                return determinant;
            determinantSum = -detLeft - detRight;
        }
        else
        {
            return determinant;
        }

        const double ccwErrorBoundA = 3.3306690738754716e-16;
        if (Math.Abs(determinant) >= ccwErrorBoundA * determinantSum)
            return determinant;

        return TriangleNet.RobustPredicates.Default.CounterClockwise(
            new TriangleNet.Geometry.Point(a.X, a.Y),
            new TriangleNet.Geometry.Point(b.X, b.Y),
            new TriangleNet.Geometry.Point(c.X, c.Y));
    }

    private static void GetFacePoints(double[] vertices, int[] faces, int face, out Point2 a, out Point2 b, out Point2 c)
    {
        int offset = face * 3;
        a = Point(vertices, faces[offset]);
        b = Point(vertices, faces[offset + 1]);
        c = Point(vertices, faces[offset + 2]);
    }

    private static Point2 Point(double[] vertices, int index) => new(vertices[index * 3], vertices[index * 3 + 1]);

    private static Bounds2D Bounds(Point2 a, Point2 b, Point2 c, double padding) => new(
        Math.Min(a.X, Math.Min(b.X, c.X)) - padding,
        Math.Max(a.X, Math.Max(b.X, c.X)) + padding,
        Math.Min(a.Y, Math.Min(b.Y, c.Y)) - padding,
        Math.Max(a.Y, Math.Max(b.Y, c.Y)) + padding);

    private static int ClipTriangle(Point2 a, Point2 b, Point2 c, Point2 q0, Point2 q1, Point2 q2, double tolerance, Span<Point2> output)
    {
        Span<Point2> first = stackalloc Point2[8];
        Span<Point2> second = stackalloc Point2[8];
        first[0] = a; first[1] = b; first[2] = c;
        int count = 3;
        double orientation = Math.Sign(Cross(q0, q1, q2));
        count = ClipAgainstEdge(first, count, second, q0, q1, orientation, tolerance);
        count = ClipAgainstEdge(second, count, first, q1, q2, orientation, tolerance);
        count = ClipAgainstEdge(first, count, second, q2, q0, orientation, tolerance);
        second[..count].CopyTo(output);
        return count;
    }

    private static int ClipAgainstEdge(ReadOnlySpan<Point2> input, int count, Span<Point2> output, Point2 edgeA, Point2 edgeB, double orientation, double tolerance)
    {
        if (count == 0)
            return 0;
        double edgeLength = Math.Max(Distance(edgeA, edgeB), tolerance);
        double crossTolerance = tolerance * edgeLength;
        int written = 0;
        Point2 previous = input[count - 1];
        double previousSide = orientation * Cross(edgeA, edgeB, previous);
        bool previousInside = previousSide >= -crossTolerance;
        for (int i = 0; i < count; i++)
        {
            Point2 current = input[i];
            double currentSide = orientation * Cross(edgeA, edgeB, current);
            bool currentInside = currentSide >= -crossTolerance;
            if (currentInside != previousInside)
            {
                double denominator = previousSide - currentSide;
                double t = Math.Abs(denominator) <= double.Epsilon ? 0.5 : previousSide / denominator;
                output[written++] = new Point2(
                    previous.X + ((current.X - previous.X) * t),
                    previous.Y + ((current.Y - previous.Y) * t));
            }
            if (currentInside)
                output[written++] = current;
            previous = current;
            previousSide = currentSide;
            previousInside = currentInside;
        }
        return written;
    }

    private static double PolygonArea(ReadOnlySpan<Point2> polygon, int count)
    {
        Point2 origin = polygon[0];
        double twiceArea = 0.0;
        for (int i = 1; i < count - 1; i++)
            twiceArea += Cross(origin, polygon[i], polygon[i + 1]);
        return Math.Abs(twiceArea) * 0.5;
    }

    private static double InterpolateZ(double[] vertices, int[] faces, int face, Point2 point)
    {
        int offset = face * 3;
        int i0 = faces[offset], i1 = faces[offset + 1], i2 = faces[offset + 2];
        Point2 a = Point(vertices, i0), b = Point(vertices, i1), c = Point(vertices, i2);
        double denominator = Cross(a, b, c);
        double w0 = Cross(b, c, point) / denominator;
        double w1 = Cross(c, a, point) / denominator;
        double w2 = 1.0 - w0 - w1;
        return (w0 * vertices[i0 * 3 + 2]) + (w1 * vertices[i1 * 3 + 2]) + (w2 * vertices[i2 * 3 + 2]);
    }

    private static double Cross(Point2 a, Point2 b, Point2 c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static double Distance(Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private struct CompensatedSum
    {
        private double _sum;
        private double _correction;
        public readonly double Value => _sum;
        public void Add(double value)
        {
            double adjusted = value - _correction;
            double next = _sum + adjusted;
            _correction = (next - _sum) - adjusted;
            _sum = next;
        }
    }
}
