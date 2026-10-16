namespace MoleHill.Core.Analysis;

/// <summary>
/// Sorts each face of a <see cref="CurvatureField"/> into a <see cref="LandformClass"/>, colours the classes
/// and totals their plan area.
/// </summary>
/// <remarks>
/// <para><b>Shape first, from the principal curvatures.</b> Each is compared with the planar threshold
/// <c>k0 = 1 / planarRadius</c>: ground bending more gently than a circle of that radius, in a direction,
/// is straight in that direction. Both convex is <see cref="LandformClass.Peak"/>, both concave
/// <see cref="LandformClass.Hollow"/>, one of each <see cref="LandformClass.Saddle"/>, neither
/// <see cref="LandformClass.Planar"/>. The principal curvatures are used rather than profile and plan for
/// this step because they do not depend on the fall line: a saddle is a saddle along its diagonals too,
/// where profile and plan are both zero.</para>
/// <para><b>Then orientation, for ground curved one way only.</b> Convex one way is a
/// <see cref="LandformClass.Ridge"/> when the bend runs across the fall line (plan curvature at least the
/// profile curvature), and a <see cref="LandformClass.Shoulder"/> when it runs along it. Concave one way is
/// a <see cref="LandformClass.Valley"/> or a <see cref="LandformClass.Toe"/> the same way. One more rule
/// keeps a level crest a ridge: where the crest line itself (the point the fitted slope would level out)
/// lies within half a landform size, the face is on the ridge, not its shoulder; a valley floor likewise.
/// Without it a horizontal berm reads as two shoulders with no ridge between them, because along a level
/// crest the fall line runs straight off the side.</para>
/// </remarks>
public static class LandformClassifier
{
    /// <summary>Class index of a face that could not be measured. Drawn neutral grey.</summary>
    public const sbyte Unmeasured = -1;

    public static int ClassCount => 8;

    private static readonly LandformClass[] InLegendOrder =
    {
        LandformClass.Peak, LandformClass.Ridge, LandformClass.Shoulder, LandformClass.Planar,
        LandformClass.Saddle, LandformClass.Toe, LandformClass.Valley, LandformClass.Hollow
    };

    /// <summary>Every class, convex to concave.</summary>
    public static IReadOnlyList<LandformClass> All => InLegendOrder;

    /// <summary>Neutral grey for unmeasured ground, the same grey every analysis preview uses.</summary>
    public static readonly (byte R, byte G, byte B) UnmeasuredColor = (130, 130, 130);

    /// <summary>
    /// Warm for convex, cool for concave, a pale neutral for planar and violet for the mixed form, so the
    /// map reads as relief at a glance: the same reading as the diverging curvature ramp.
    /// </summary>
    public static (byte R, byte G, byte B) ColorOf(LandformClass landform) => landform switch
    {
        LandformClass.Peak => (183, 28, 28),
        LandformClass.Ridge => (239, 108, 0),
        LandformClass.Shoulder => (251, 192, 45),
        LandformClass.Planar => (232, 228, 214),
        LandformClass.Saddle => (142, 100, 180),
        LandformClass.Toe => (128, 203, 196),
        LandformClass.Valley => (30, 136, 229),
        LandformClass.Hollow => (26, 35, 126),
        _ => UnmeasuredColor
    };

    public static string NameOf(LandformClass landform) => landform switch
    {
        LandformClass.Peak => "Peak",
        LandformClass.Ridge => "Ridge",
        LandformClass.Shoulder => "Shoulder",
        LandformClass.Planar => "Planar",
        LandformClass.Saddle => "Saddle",
        LandformClass.Toe => "Toe",
        LandformClass.Valley => "Valley",
        LandformClass.Hollow => "Hollow",
        _ => "Unmeasured"
    };

    /// <summary>
    /// The class of every face, or <see cref="Unmeasured"/>.
    /// </summary>
    /// <param name="planarRadius">Radius of curvature, in model units, beyond which ground counts as straight.
    /// Zero or less makes any measurable bend count.</param>
    public static sbyte[] Classify(CurvatureField field, double planarRadius)
    {
        ArgumentNullException.ThrowIfNull(field);
        double threshold = double.IsFinite(planarRadius) && planarRadius > 0.0 ? 1.0 / planarRadius : 0.0;
        double crestReach = field.LandformSize * 0.5;
        var classes = new sbyte[field.FaceCount];
        for (int face = 0; face < field.FaceCount; face++)
        {
            if (!field.IsMeasured(face))
            {
                classes[face] = Unmeasured;
                continue;
            }

            field.GetDerivatives(face, out double p, out double q, out double r, out double s, out double t);
            classes[face] = (sbyte)Classify(p, q, r, s, t, threshold, crestReach);
        }

        return classes;
    }

    /// <summary>The class of one fitted point. See the type remarks for the rules.</summary>
    /// <param name="threshold">Planar curvature threshold, 1/model-unit.</param>
    /// <param name="crestReach">Distance within which a level crest or floor still counts as the ridge or
    /// valley itself.</param>
    public static LandformClass Classify(double p, double q, double r, double s, double t, double threshold, double crestReach)
    {
        CurvatureField.PrincipalCurvatures(p, q, r, s, t, out double max, out double min);
        bool convex = max > threshold;
        bool concave = min < -threshold;

        if (convex && concave)
            return LandformClass.Saddle;
        if (convex && min > threshold)
            return LandformClass.Peak;
        if (concave && max < -threshold)
            return LandformClass.Hollow;
        if (!convex && !concave)
            return LandformClass.Planar;

        double gradient = Math.Sqrt((p * p) + (q * q));
        double profile = CurvatureField.ProfileCurvature(p, q, r, s, t);
        double plan = CurvatureField.PlanCurvature(p, q, r, s, t);
        if (convex)
        {
            return plan >= profile || gradient <= max * crestReach
                ? LandformClass.Ridge
                : LandformClass.Shoulder;
        }

        return plan <= profile || gradient <= -min * crestReach
            ? LandformClass.Valley
            : LandformClass.Toe;
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
