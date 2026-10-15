namespace MoleHill.Core.Analysis;

/// <summary>
/// The terrain's surface derivatives per face, measured at one landform size by
/// <see cref="CurvatureAnalyzer.Measure"/>, and every curvature that follows from them.
/// </summary>
/// <remarks>
/// <para>What is stored is the local quadric the surface was fitted with, as the five derivatives of
/// <c>z(x, y)</c>: <c>p = dz/dx</c>, <c>q = dz/dy</c>, <c>r = d2z/dx2</c>, <c>s = d2z/dxdy</c>,
/// <c>t = d2z/dy2</c> (Evans-Young notation). Every curvature measure, the principal curvatures and the
/// landform class are cheap functions of those five numbers, so one fit serves the value ramp, the class
/// map and the card's figures, and switching between them recolours without measuring again.</para>
/// <para>Curvatures are in 1/model-unit and <b>convex positive</b>: a dome is positive, a bowl negative.
/// That is the opposite sign to the textbook mean curvature of a graph surface, and it is the convention
/// landform maps use, because "up is out" reads the way the ground looks.</para>
/// <para>A face the fit could not measure (a degenerate face, or ground so sparse or collinear that no
/// quadric is determined) carries NaN derivatives and reports NaN for every measure.</para>
/// </remarks>
public sealed class CurvatureField
{
    private const int Stride = 5;

    /// <summary>
    /// Below this gradient (rise over run) the fall line has no direction, so profile and plan curvature
    /// are undefined and report the mean curvature instead. Far below any slope a survey can resolve.
    /// </summary>
    private const double DirectionlessGradient = 1e-6;

    private readonly double[] _derivatives;

    internal CurvatureField(double landformSize, double[] derivatives, double[] planAreas, int faceCount)
    {
        LandformSize = landformSize;
        _derivatives = derivatives;
        PlanAreas = planAreas;
        FaceCount = faceCount;
    }

    public int FaceCount { get; }

    /// <summary>The radius the surface was fitted over, as requested (sparse ground may have needed more).</summary>
    public double LandformSize { get; }

    /// <summary>Per-face XY-projected area: the weight a plan-read map's distribution and summary want.</summary>
    public double[] PlanAreas { get; }

    public bool IsMeasured(int face) => !double.IsNaN(_derivatives[face * Stride]);

    /// <summary>The fitted derivatives at a face (NaN when unmeasured).</summary>
    public void GetDerivatives(int face, out double p, out double q, out double r, out double s, out double t)
    {
        int offset = face * Stride;
        p = _derivatives[offset];
        q = _derivatives[offset + 1];
        r = _derivatives[offset + 2];
        s = _derivatives[offset + 3];
        t = _derivatives[offset + 4];
    }

    /// <summary>One measure at one face.</summary>
    public double Value(int face, CurvatureMeasure measure)
    {
        GetDerivatives(face, out double p, out double q, out double r, out double s, out double t);
        return measure switch
        {
            CurvatureMeasure.Profile => ProfileCurvature(p, q, r, s, t),
            CurvatureMeasure.Plan => PlanCurvature(p, q, r, s, t),
            _ => MeanCurvature(p, q, r, s, t)
        };
    }

    /// <summary>One measure for every face, NaN where unmeasured.</summary>
    public double[] Values(CurvatureMeasure measure)
    {
        var values = new double[FaceCount];
        for (int face = 0; face < FaceCount; face++)
            values[face] = Value(face, measure);
        return values;
    }

    /// <summary>Mean curvature, convex positive.</summary>
    public static double MeanCurvature(double p, double q, double r, double s, double t)
    {
        double w = 1.0 + (p * p) + (q * q);
        return -(((1.0 + (q * q)) * r) - (2.0 * p * q * s) + ((1.0 + (p * p)) * t)) / (2.0 * w * Math.Sqrt(w));
    }

    /// <summary>Gaussian curvature: the product of the principal curvatures, positive on a dome or a bowl
    /// and negative on a saddle. Its sign does not depend on the convexity convention.</summary>
    public static double GaussianCurvature(double p, double q, double r, double s, double t)
    {
        double w = 1.0 + (p * p) + (q * q);
        return ((r * t) - (s * s)) / (w * w);
    }

    /// <summary>The two principal curvatures, convex positive, with <paramref name="max"/> &gt;= <paramref name="min"/>.</summary>
    public static void PrincipalCurvatures(
        double p, double q, double r, double s, double t, out double max, out double min)
    {
        double mean = MeanCurvature(p, q, r, s, t);
        double gaussian = GaussianCurvature(p, q, r, s, t);
        double spread = Math.Sqrt(Math.Max(0.0, (mean * mean) - gaussian));
        max = mean + spread;
        min = mean - spread;
    }

    /// <summary>
    /// Normal curvature along the fall line, convex positive. On ground with no fall line (level to within
    /// <see cref="DirectionlessGradient"/>) it reports the mean curvature.
    /// </summary>
    public static double ProfileCurvature(double p, double q, double r, double s, double t)
    {
        double g2 = (p * p) + (q * q);
        if (!(g2 > DirectionlessGradient * DirectionlessGradient))
            return MeanCurvature(p, q, r, s, t);

        double w = 1.0 + g2;
        return -(((p * p) * r) + (2.0 * p * q * s) + ((q * q) * t)) / (g2 * w * Math.Sqrt(w));
    }

    /// <summary>
    /// Normal curvature across the fall line (tangential curvature), convex positive. On ground with no fall
    /// line it reports the mean curvature. Profile and plan always average to the mean.
    /// </summary>
    public static double PlanCurvature(double p, double q, double r, double s, double t)
    {
        double g2 = (p * p) + (q * q);
        if (!(g2 > DirectionlessGradient * DirectionlessGradient))
            return MeanCurvature(p, q, r, s, t);

        double w = 1.0 + g2;
        return -(((q * q) * r) - (2.0 * p * q * s) + ((p * p) * t)) / (g2 * Math.Sqrt(w));
    }
}
