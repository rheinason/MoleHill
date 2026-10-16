namespace MoleHill.Core.Analysis;

/// <summary>
/// Sorts each face of a <see cref="CurvatureField"/> into a <see cref="LandformClass"/> (convex, planar or
/// concave), colours the classes and totals their plan area.
/// </summary>
/// <remarks>
/// <para>The class comes from the <b>mean curvature</b> <c>H</c> (convex positive) against a
/// <b>flatness tolerance</b> stated as a height: how far the ground may depart from a plane across one
/// landform size. A surface of curvature <c>H</c> rises <c>|H| L2 / 8</c> above the chord over a length
/// <c>L</c> (the sagitta), so a face is <see cref="LandformClass.Planar"/> when
/// <c>|H| L2 / 8 &lt;= tolerance</c>, and otherwise convex or concave by the sign of <c>H</c>.</para>
/// <para>Stating it as a height ties the threshold to the landform size: the same 50 mm reads as "flat" at
/// any size, while a curvature threshold had to be retuned every time the size changed, because measured
/// curvature shrinks as the fit widens.</para>
/// </remarks>
public static class LandformClassifier
{
    /// <summary>Class index of a face that could not be measured. Drawn neutral grey.</summary>
    public const sbyte Unmeasured = -1;

    public static int ClassCount => 3;

    private static readonly LandformClass[] InLegendOrder =
    {
        LandformClass.Convex, LandformClass.Planar, LandformClass.Concave
    };

    /// <summary>Every class, convex to concave.</summary>
    public static IReadOnlyList<LandformClass> All => InLegendOrder;

    /// <summary>Neutral grey for unmeasured ground, the same grey every analysis preview uses.</summary>
    public static readonly (byte R, byte G, byte B) UnmeasuredColor = (130, 130, 130);

    /// <summary>Warm for convex, a pale neutral for planar, cool for concave: the same reading as the
    /// diverging curvature ramp.</summary>
    public static (byte R, byte G, byte B) ColorOf(LandformClass landform) => landform switch
    {
        LandformClass.Convex => (214, 96, 39),
        LandformClass.Planar => (232, 228, 214),
        LandformClass.Concave => (41, 121, 198),
        _ => UnmeasuredColor
    };

    public static string NameOf(LandformClass landform) => landform switch
    {
        LandformClass.Convex => "Convex",
        LandformClass.Planar => "Planar",
        LandformClass.Concave => "Concave",
        _ => "Unmeasured"
    };

    /// <summary>
    /// The class of every face, or <see cref="Unmeasured"/>.
    /// </summary>
    /// <param name="flatnessTolerance">Height, in model units, the ground may depart from a plane across one
    /// landform size and still count as planar. Zero or less makes any measurable bend count.</param>
    public static sbyte[] Classify(CurvatureField field, double flatnessTolerance)
    {
        ArgumentNullException.ThrowIfNull(field);
        double threshold = CurvatureThreshold(flatnessTolerance, field.LandformSize);
        var classes = new sbyte[field.FaceCount];
        for (int face = 0; face < field.FaceCount; face++)
        {
            classes[face] = field.IsMeasured(face)
                ? (sbyte)Classify(field.Value(face, CurvatureMeasure.Mean), threshold)
                : Unmeasured;
        }

        return classes;
    }

    /// <summary>
    /// The mean curvature at which the sagitta across <paramref name="landformSize"/> reaches
    /// <paramref name="flatnessTolerance"/>: <c>8 tolerance / L2</c>. Zero when either is not positive, so
    /// any bend counts; a landform size of zero has no length to measure flatness across.
    /// </summary>
    public static double CurvatureThreshold(double flatnessTolerance, double landformSize)
    {
        if (!(double.IsFinite(flatnessTolerance) && flatnessTolerance > 0.0) ||
            !(double.IsFinite(landformSize) && landformSize > 0.0))
        {
            return 0.0;
        }

        return 8.0 * flatnessTolerance / (landformSize * landformSize);
    }

    /// <summary>The class of one mean curvature against a curvature threshold.</summary>
    public static LandformClass Classify(double meanCurvature, double threshold)
    {
        if (meanCurvature > threshold)
            return LandformClass.Convex;
        if (meanCurvature < -threshold)
            return LandformClass.Concave;
        return LandformClass.Planar;
    }

    /// <summary>Per-face RGB, flat: [r0,g0,b0, r1,g1,b1, ...].</summary>
    public static byte[] BuildFaceColors(sbyte[] classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        var colors = new byte[classes.Length * 3];
        for (int face = 0; face < classes.Length; face++)
        {
            (byte r, byte g, byte b) = classes[face] < 0 ? UnmeasuredColor : ColorOf((LandformClass)classes[face]);
            colors[face * 3] = r;
            colors[(face * 3) + 1] = g;
            colors[(face * 3) + 2] = b;
        }

        return colors;
    }

    /// <summary>Plan area of each class (indexed by <see cref="LandformClass"/>), and of all measured ground.</summary>
    public static double[] SumPlanAreas(sbyte[] classes, double[] planAreas, out double measuredArea)
    {
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(planAreas);
        var areas = new double[ClassCount];
        measuredArea = 0.0;
        int count = Math.Min(classes.Length, planAreas.Length);
        for (int face = 0; face < count; face++)
        {
            if (classes[face] < 0)
                continue;

            areas[classes[face]] += planAreas[face];
            measuredArea += planAreas[face];
        }

        return areas;
    }
}
