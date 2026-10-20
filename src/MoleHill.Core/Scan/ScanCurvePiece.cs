using MoleHill.Core.Routing;

namespace MoleHill.Core.Scan;

/// <summary>One clamped rational B-spline scan piece, in the full knot-vector convention.</summary>
public sealed class ScanCurvePiece
{
    public ScanCurvePiece(int degree, double[] knots, double[] controlPoints, double[]? weights = null)
    {
        if (degree < 1 || controlPoints.Length % 3 != 0 || controlPoints.Length / 3 < degree + 1 ||
            knots.Length != controlPoints.Length / 3 + degree + 1 ||
            controlPoints.Any(p => !double.IsFinite(p)) || knots.Any(k => !double.IsFinite(k)) ||
            knots[0] != 0 || knots[^1] != 1 || knots.Zip(knots.Skip(1), (a, b) => a > b).Any(b => b) ||
            weights != null && (weights.Length != controlPoints.Length / 3 || weights.Any(w => !double.IsFinite(w) || w <= 0)))
            throw new ArgumentException("Invalid rational scan curve.");
        Degree = degree; Knots = knots; ControlPoints = controlPoints;
        Weights = weights ?? Enumerable.Repeat(1.0, ControlPointCount).ToArray();
    }
    public int Degree { get; }
    public double[] Knots { get; }
    public double[] ControlPoints { get; }
    public double[] Weights { get; }
    public int ControlPointCount => ControlPoints.Length / 3;
    public (double X, double Y, double Z) Start => PointAt(0);
    public (double X, double Y, double Z) End => PointAt(1);
    public (double X, double Y, double Z) PointAt(double u)
    {
        int span = RouteSplineCurve.FindSpan(ControlPointCount - 1, Degree, u, Knots);
        Span<double> basis = stackalloc double[Degree + 1];
        RouteSplineCurve.Basis(span, u, Degree, Knots, basis);
        double x = 0, y = 0, z = 0, weight = 0;
        for (int j = 0; j <= Degree; j++)
        {
            int i = span - Degree + j;
            double b = basis[j] * Weights[i];
            x += b * ControlPoints[i * 3]; y += b * ControlPoints[i * 3 + 1]; z += b * ControlPoints[i * 3 + 2]; weight += b;
        }
        return (x / weight, y / weight, z / weight);
    }
}
