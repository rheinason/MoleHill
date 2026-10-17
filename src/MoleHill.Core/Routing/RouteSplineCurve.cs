namespace MoleHill.Core.Routing;

/// <summary>
/// A clamped, non-rational B-spline in 3D: the editable form a fitted route is handed to the host in.
/// <see cref="Knots"/> is the full clamped vector (degree + 1 copies of 0 and of 1 at its ends), so it has
/// <c>ControlPointCount + Degree + 1</c> entries; a host whose knot convention drops the outer two (Rhino's)
/// skips the first and the last.
/// </summary>
public sealed class RouteSplineCurve
{
    private RouteSplineCurve? _derivative;

    internal RouteSplineCurve(int degree, double[] knots, double[] controlPoints)
    {
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(degree));
        if (controlPoints.Length % 3 != 0 || controlPoints.Length / 3 < degree + 1)
            throw new ArgumentException("A spline needs at least degree + 1 control points.", nameof(controlPoints));
        if (knots.Length != (controlPoints.Length / 3) + degree + 1)
            throw new ArgumentException("The knot vector must have control points + degree + 1 entries.", nameof(knots));

        Degree = degree;
        Knots = knots;
        ControlPoints = controlPoints;
    }

    public int Degree { get; }

    /// <summary>The full clamped knot vector, on [0, 1].</summary>
    public double[] Knots { get; }

    /// <summary>Flat XYZ control points.</summary>
    public double[] ControlPoints { get; }

    public int ControlPointCount => ControlPoints.Length / 3;

    public (double X, double Y, double Z) Start => (ControlPoints[0], ControlPoints[1], ControlPoints[2]);

    public (double X, double Y, double Z) End =>
        (ControlPoints[^3], ControlPoints[^2], ControlPoints[^1]);

    public (double X, double Y, double Z) PointAt(double u)
    {
        int n = ControlPointCount - 1;
        int span = FindSpan(n, Degree, u, Knots);
        Span<double> basis = stackalloc double[Degree + 1];
        Basis(span, u, Degree, Knots, basis);
        double x = 0.0, y = 0.0, z = 0.0;
        for (int j = 0; j <= Degree; j++)
        {
            int index = (span - Degree + j) * 3;
            x += basis[j] * ControlPoints[index];
            y += basis[j] * ControlPoints[index + 1];
            z += basis[j] * ControlPoints[index + 2];
        }

        return (x, y, z);
    }

    /// <summary>The first derivative with respect to the curve parameter.</summary>
    public (double X, double Y, double Z) DerivativeAt(double u)
    {
        _derivative ??= Differentiate();
        return _derivative.PointAt(u);
    }

    /// <summary>
    /// Signed plan curvature at <paramref name="u"/> (positive turning left), from the first and second
    /// derivatives: <c>(x'y'' - y'x'') / (x'^2 + y'^2)^1.5</c>. Zero where the plan speed vanishes.
    /// </summary>
    public double PlanCurvatureAt(double u)
    {
        _derivative ??= Differentiate();
        (double dx, double dy, _) = _derivative.PointAt(u);
        (double ddx, double ddy, _) = _derivative.DerivativeAt(u);
        double speedSquared = (dx * dx) + (dy * dy);
        if (!(speedSquared > 1e-24))
            return 0.0;

        return ((dx * ddy) - (dy * ddx)) / (speedSquared * Math.Sqrt(speedSquared));
    }

    /// <summary>
    /// The same curve with one more knot at <paramref name="u"/> (Boehm's insertion, The NURBS Book A5.1):
    /// one more control point, the shape unchanged.
    /// </summary>
    internal RouteSplineCurve InsertKnot(double u)
    {
        int n = ControlPointCount - 1;
        int p = Degree;
        int k = FindSpan(n, p, u, Knots);
        var knots = new double[Knots.Length + 1];
        for (int i = 0; i <= k; i++)
            knots[i] = Knots[i];
        knots[k + 1] = u;
        for (int i = k + 1; i < Knots.Length; i++)
            knots[i + 1] = Knots[i];

        var points = new double[(n + 2) * 3];
        for (int i = 0; i <= k - p; i++)
            CopyPoint(i, i);
        for (int i = k; i <= n; i++)
            CopyPoint(i, i + 1);
        for (int i = k - p + 1; i <= k; i++)
        {
            double denominator = Knots[i + p] - Knots[i];
            double alpha = denominator > 0.0 ? (u - Knots[i]) / denominator : 0.0;
            for (int axis = 0; axis < 3; axis++)
                points[(i * 3) + axis] = (alpha * ControlPoints[(i * 3) + axis]) + ((1.0 - alpha) * ControlPoints[((i - 1) * 3) + axis]);
        }

        return new RouteSplineCurve(Degree, knots, points);

        void CopyPoint(int from, int to)
        {
            for (int axis = 0; axis < 3; axis++)
                points[(to * 3) + axis] = ControlPoints[(from * 3) + axis];
        }
    }

    /// <summary>The derivative as a spline of one degree less (the hodograph). Needs degree 2 or more.</summary>
    internal RouteSplineCurve Differentiate()
    {
        int count = ControlPointCount;
        if (Degree < 2)
            throw new InvalidOperationException("Only splines of degree 2 or more are differentiated here.");

        var points = new double[(count - 1) * 3];
        for (int i = 0; i < count - 1; i++)
        {
            double denominator = Knots[i + Degree + 1] - Knots[i + 1];
            double scale = denominator > 0.0 ? Degree / denominator : 0.0;
            for (int axis = 0; axis < 3; axis++)
                points[(i * 3) + axis] = scale * (ControlPoints[((i + 1) * 3) + axis] - ControlPoints[(i * 3) + axis]);
        }

        var knots = new double[Knots.Length - 2];
        Array.Copy(Knots, 1, knots, 0, knots.Length);
        return new RouteSplineCurve(Degree - 1, knots, points);
    }

    /// <summary>The knot span index containing <paramref name="u"/> (The NURBS Book, A2.1).</summary>
    internal static int FindSpan(int n, int degree, double u, IReadOnlyList<double> knots)
    {
        if (u >= knots[n + 1])
            return n;
        if (u <= knots[degree])
            return degree;

        int low = degree;
        int high = n + 1;
        int middle = (low + high) / 2;
        while (u < knots[middle] || u >= knots[middle + 1])
        {
            if (u < knots[middle])
                high = middle;
            else
                low = middle;
            middle = (low + high) / 2;
        }

        return middle;
    }

    /// <summary>
    /// The non-zero basis functions and their derivatives up to <paramref name="order"/> at
    /// <paramref name="u"/> (The NURBS Book, A2.3): <c>derivatives[k, j]</c> is the k-th derivative of
    /// basis function <c>span - degree + j</c>.
    /// </summary>
    internal static void BasisDerivatives(int span, double u, int degree, IReadOnlyList<double> knots, int order, double[,] derivatives)
    {
        var ndu = new double[degree + 1, degree + 1];
        var left = new double[degree + 1];
        var right = new double[degree + 1];
        ndu[0, 0] = 1.0;
        for (int j = 1; j <= degree; j++)
        {
            left[j] = u - knots[span + 1 - j];
            right[j] = knots[span + j] - u;
            double saved = 0.0;
            for (int r = 0; r < j; r++)
            {
                ndu[j, r] = right[r + 1] + left[j - r];
                double temp = ndu[j, r] != 0.0 ? ndu[r, j - 1] / ndu[j, r] : 0.0;
                ndu[r, j] = saved + (right[r + 1] * temp);
                saved = left[j - r] * temp;
            }

            ndu[j, j] = saved;
        }

        for (int j = 0; j <= degree; j++)
            derivatives[0, j] = ndu[j, degree];

        var a = new double[2, degree + 1];
        for (int r = 0; r <= degree; r++)
        {
            int s1 = 0, s2 = 1;
            a[0, 0] = 1.0;
            for (int k = 1; k <= order; k++)
            {
                double d = 0.0;
                int rk = r - k, pk = degree - k;
                if (r >= k)
                {
                    a[s2, 0] = Divide(a[s1, 0], ndu[pk + 1, rk]);
                    d = a[s2, 0] * ndu[rk, pk];
                }

                int j1 = rk >= -1 ? 1 : -rk;
                int j2 = r - 1 <= pk ? k - 1 : degree - r;
                for (int j = j1; j <= j2; j++)
                {
                    a[s2, j] = Divide(a[s1, j] - a[s1, j - 1], ndu[pk + 1, rk + j]);
                    d += a[s2, j] * ndu[rk + j, pk];
                }

                if (r <= pk)
                {
                    a[s2, k] = Divide(-a[s1, k - 1], ndu[pk + 1, r]);
                    d += a[s2, k] * ndu[r, pk];
                }

                derivatives[k, r] = d;
                (s1, s2) = (s2, s1);
            }
        }

        double factor = degree;
        for (int k = 1; k <= order; k++)
        {
            for (int j = 0; j <= degree; j++)
                derivatives[k, j] *= factor;
            factor *= degree - k;
        }

        static double Divide(double numerator, double denominator) => denominator != 0.0 ? numerator / denominator : 0.0;
    }

    /// <summary>The degree + 1 non-zero basis functions at <paramref name="u"/> in span <paramref name="span"/> (A2.2).</summary>
    internal static void Basis(int span, double u, int degree, IReadOnlyList<double> knots, Span<double> basis)
    {
        Span<double> left = stackalloc double[degree + 1];
        Span<double> right = stackalloc double[degree + 1];
        basis[0] = 1.0;
        for (int j = 1; j <= degree; j++)
        {
            left[j] = u - knots[span + 1 - j];
            right[j] = knots[span + j] - u;
            double saved = 0.0;
            for (int r = 0; r < j; r++)
            {
                double denominator = right[r + 1] + left[j - r];
                double temp = denominator != 0.0 ? basis[r] / denominator : 0.0;
                basis[r] = saved + (right[r + 1] * temp);
                saved = left[j - r] * temp;
            }

            basis[j] = saved;
        }
    }
}
