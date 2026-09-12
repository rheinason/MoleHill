namespace MoleHill.Core.Analysis;

/// <summary>
/// Per-face aspect — the compass bearing a face drains towards — from a triangle mesh.
///
/// Aspect is the other angle of the normal <see cref="SlopeAnalyzer"/> already measures, so this is
/// deliberately shaped like it: the same flat-array inputs, the same plan-area weighting, the same
/// palette/band apparatus, and a summary overload that allocates no colours.
///
/// Two things aspect needs that slope does not:
///
/// 1. <b>A cyclic range.</b> Bearings live on 0..360 and the ends meet, so the range is pinned
///    (<see cref="RangeShape.Cyclic"/>) rather than fitted, and the ramp is expected to close on itself
///    (<c>aspect-wheel</c> in <see cref="ColorRampPresets"/>).
/// 2. <b>A flat exemption.</b> A level face has no aspect at all. Its bearing is whatever rounding noise
///    the normal happened to carry, so faces flatter than a stated slope are flagged and drawn neutral
///    instead of being given a direction they do not have — the same explicit-flag treatment the cut/fill
///    preview uses for faces with no reference beneath them.
/// </summary>
public static class AspectAnalyzer
{
    private const int ParallelAspectThreshold = 20_000;

    /// <summary>Neutral grey for a face with no aspect. Matches the analysis preview's unmapped colour.</summary>
    private static readonly SlopeAnalyzer.ColorStop DefaultFlatColor = new(0.0, 130, 130, 130);

    private static readonly SlopeAnalyzer.ColorStop[] DefaultPalette =
        ColorRampPresets.ResolveRamp("aspect-wheel").Stops.ToArray();

    /// <summary>The eight named sectors, in bearing order from north.</summary>
    private static readonly string[] SectorNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    public sealed class AspectSummary
    {
        public AspectSummary(
            int faceCount,
            int flatFaceCount,
            double dominantBearing,
            double flatPlanArea,
            double totalPlanArea)
        {
            FaceCount = faceCount;
            FlatFaceCount = flatFaceCount;
            DominantBearing = dominantBearing;
            FlatPlanArea = flatPlanArea;
            TotalPlanArea = totalPlanArea;
        }

        public int FaceCount { get; }

        /// <summary>Faces with no meaningful aspect, because they are flatter than the threshold.</summary>
        public int FlatFaceCount { get; }

        /// <summary>
        /// The plan-area-weighted circular mean bearing of every sloping face, in degrees. NaN when every
        /// face is flat, or when the sloping faces cancel out exactly (a symmetric cone) — a mean direction
        /// genuinely does not exist there, and reporting 0 would read as "north".
        /// </summary>
        public double DominantBearing { get; }

        public double FlatPlanArea { get; }

        public double TotalPlanArea { get; }

        /// <summary>The range mapped across the palette — always a full turn.</summary>
        public AnalysisRange Range => new(0.0, AnalysisRange.FullTurnDegrees, false);
    }

    public sealed class AspectResult
    {
        public AspectResult(double[] bearings, double[] planAreas, byte[] faceColors, AspectSummary summary)
        {
            Bearings = bearings;
            PlanAreas = planAreas;
            FaceColors = faceColors;
            Summary = summary;
        }

        /// <summary>Per-face bearing in degrees clockwise from north, or NaN for a flat face.</summary>
        public double[] Bearings { get; }

        /// <summary>Per-face XY-projected area — the weight a plan-read map's distribution wants.</summary>
        public double[] PlanAreas { get; }

        /// <summary>Per-face RGB, flat: [r0,g0,b0, r1,g1,b1, …].</summary>
        public byte[] FaceColors { get; }

        public AspectSummary Summary { get; }

        public AnalysisRange Range => Summary.Range;
    }

    /// <summary>
    /// Aspect without per-face colours.
    /// </summary>
    /// <param name="northAzimuthDegrees">Direction of north, as an angle CCW from +X — exactly what
    /// Rhino's <c>Sun.North</c> holds and <c>mhSetSunNorth</c> writes.</param>
    /// <param name="flatSlopeRatio">Faces whose slope (rise over run) is at or below this have no aspect.</param>
    public static AspectSummary Summarize(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double northAzimuthDegrees,
        double flatSlopeRatio)
    {
        var bearings = new double[faceCount];
        var planAreas = new double[faceCount];
        return Accumulate(vertices, faces, faceCount, northAzimuthDegrees, flatSlopeRatio, bearings, planAreas);
    }

    /// <summary>
    /// Per-face aspect and colours.
    /// </summary>
    /// <param name="palette">Normalized colour stops. Should close on itself; null takes the built-in
    /// aspect wheel, which does.</param>
    /// <param name="mode">How the wheel is sampled. The default is
    /// <see cref="AnalysisColorMapper.Mode.Constant"/>, which holds each wheel stop's colour until the
    /// next one: with the eight-stop aspect wheel that is eight crisp sectors whose edges sit on the
    /// cardinals, and the stops in the ramp editor are those edges. Gradient gives a continuous wheel.</param>
    /// <param name="interval">Band width in degrees, for stepped mode only; 0 picks a readable step.</param>
    /// <param name="flatColor">Colour for faces with no aspect. Null takes a neutral grey.</param>
    public static AspectResult Analyze(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double northAzimuthDegrees,
        double flatSlopeRatio,
        IReadOnlyList<SlopeAnalyzer.ColorStop>? palette = null,
        AnalysisColorMapper.Mode mode = AnalysisColorMapper.Mode.Constant,
        double interval = 0.0,
        SlopeAnalyzer.ColorStop? flatColor = null)
    {
        var bearings = new double[faceCount];
        var planAreas = new double[faceCount];
        AspectSummary summary = Accumulate(
            vertices, faces, faceCount, northAzimuthDegrees, flatSlopeRatio, bearings, planAreas);

        var colors = new byte[faceCount * 3];
        IReadOnlyList<SlopeAnalyzer.ColorStop> effectivePalette = ResolvePalette(palette);
        AnalysisRange range = summary.Range;
        IReadOnlyList<AnalysisColorMapper.Band>? bands =
            AnalysisColorMapper.ResolveBandsFor(range, mode, interval, effectivePalette);
        SlopeAnalyzer.ColorStop flat = flatColor ?? DefaultFlatColor;

        void ColorFace(int f)
        {
            double bearing = bearings[f];
            SlopeAnalyzer.ColorStop color = double.IsNaN(bearing)
                ? flat
                : AnalysisColorMapper.SampleResolved(bearing, range, mode, bands, effectivePalette);
            colors[f * 3] = color.R;
            colors[(f * 3) + 1] = color.G;
            colors[(f * 3) + 2] = color.B;
        }

        if (faceCount >= ParallelAspectThreshold)
            Parallel.For(0, faceCount, ColorFace);
        else
            for (int f = 0; f < faceCount; f++)
                ColorFace(f);

        return new AspectResult(bearings, planAreas, colors, summary);
    }

    /// <summary>
    /// The named sector a bearing is nearest — the reading a human summary wants, so 350 and 10 are both
    /// "N". Deliberately NOT the same division as the colour bands, whose edges sit *on* the cardinals:
    /// a band is a range the reader sees labelled with its own numbers, while "mainly north-facing" is a
    /// statement about a single mean bearing, and rounding is the honest way to say it.
    /// </summary>
    public static string SectorName(double bearingDegrees)
    {
        if (!double.IsFinite(bearingDegrees))
            return "—";

        double normalized = Normalize360(bearingDegrees);
        int sector = (int)Math.Round(normalized / 45.0) % SectorNames.Length;
        return SectorNames[sector];
    }

    /// <summary>Reduces an angle in degrees to [0, 360).</summary>
    public static double Normalize360(double degrees)
    {
        if (!double.IsFinite(degrees))
            return double.NaN;

        double wrapped = degrees % AnalysisRange.FullTurnDegrees;
        if (wrapped < 0.0)
            wrapped += AnalysisRange.FullTurnDegrees;

        // A value a hair under a full turn must not round up to exactly 360, which is outside the range.
        return wrapped >= AnalysisRange.FullTurnDegrees ? 0.0 : wrapped;
    }

    private static AspectSummary Accumulate(
        double[] vertices,
        int[] faces,
        int faceCount,
        double northAzimuthDegrees,
        double flatSlopeRatio,
        double[] bearings,
        double[] planAreas)
    {
        double threshold = double.IsFinite(flatSlopeRatio) ? Math.Max(0.0, flatSlopeRatio) : 0.0;
        int flatFaceCount = 0;
        double flatPlanArea = 0.0;
        double totalPlanArea = 0.0;

        // Circular mean: bearings are directions, so they are averaged as unit vectors. Averaging the
        // numbers instead would put the mean of 350 and 10 at due south.
        double sumX = 0.0;
        double sumY = 0.0;

        for (int f = 0; f < faceCount; f++)
        {
            double bearing = ComputeFaceBearing(
                vertices, faces, f, northAzimuthDegrees, threshold, out double planArea);
            bearings[f] = bearing;
            planAreas[f] = planArea;
            totalPlanArea += planArea;

            if (double.IsNaN(bearing))
            {
                flatFaceCount++;
                flatPlanArea += planArea;
                continue;
            }

            double radians = bearing * (Math.PI / 180.0);
            sumX += Math.Cos(radians) * planArea;
            sumY += Math.Sin(radians) * planArea;
        }

        double dominant = sumX == 0.0 && sumY == 0.0
            ? double.NaN
            : Normalize360(Math.Atan2(sumY, sumX) * (180.0 / Math.PI));

        return new AspectSummary(faceCount, flatFaceCount, dominant, flatPlanArea, totalPlanArea);
    }

    /// <summary>
    /// The bearing a face drains towards, or NaN when it is flatter than <paramref name="flatSlopeRatio"/>.
    /// </summary>
    /// <param name="planArea">The face's XY-projected area.</param>
    private static double ComputeFaceBearing(
        double[] vertices,
        int[] faces,
        int faceIndex,
        double northAzimuthDegrees,
        double flatSlopeRatio,
        out double planArea)
    {
        int i0 = faces[faceIndex * 3];
        int i1 = faces[(faceIndex * 3) + 1];
        int i2 = faces[(faceIndex * 3) + 2];

        double ax = vertices[i0 * 3];
        double ay = vertices[(i0 * 3) + 1];
        double az = vertices[(i0 * 3) + 2];
        double e1x = vertices[i1 * 3] - ax;
        double e1y = vertices[(i1 * 3) + 1] - ay;
        double e1z = vertices[(i1 * 3) + 2] - az;
        double e2x = vertices[i2 * 3] - ax;
        double e2y = vertices[(i2 * 3) + 1] - ay;
        double e2z = vertices[(i2 * 3) + 2] - az;

        double nx = (e1y * e2z) - (e1z * e2y);
        double ny = (e1z * e2x) - (e1x * e2z);
        double nz = (e1x * e2y) - (e1y * e2x);

        planArea = Math.Abs(nz) * 0.5;

        // Face winding is not guaranteed, so take the upward normal: the downhill direction must not
        // depend on which way the triangle happens to be wound.
        if (nz < 0.0)
        {
            nx = -nx;
            ny = -ny;
            nz = -nz;
        }

        double horizontal = Math.Sqrt((nx * nx) + (ny * ny));
        if (horizontal <= 0.0)
            return double.NaN;

        // A vertical face (nz == 0) has a perfectly well-defined aspect even though its slope is infinite,
        // so the flat test only applies where nz gives a finite ratio.
        if (nz > 0.0 && horizontal / nz <= flatSlopeRatio)
            return double.NaN;

        // For an upward normal (nx, ny, nz) the steepest DESCENT direction in plan is (nx, ny): the
        // gradient of the plane is (-nx/nz, -ny/nz), and aspect is the direction water leaves by.
        double descentAzimuth = Math.Atan2(ny, nx) * (180.0 / Math.PI);

        // North is given as an azimuth CCW from +X; a compass bearing runs clockwise from north.
        return Normalize360(northAzimuthDegrees - descentAzimuth);
    }

    private static IReadOnlyList<SlopeAnalyzer.ColorStop> ResolvePalette(
        IReadOnlyList<SlopeAnalyzer.ColorStop>? palette)
    {
        if (palette == null || palette.Count == 0)
            return DefaultPalette;

        if (palette.Count == 1)
            return new[] { new SlopeAnalyzer.ColorStop(0.0, palette[0].R, palette[0].G, palette[0].B) };

        return palette
            .OrderBy(stop => stop.Position)
            .Select(stop => new SlopeAnalyzer.ColorStop(stop.Position, stop.R, stop.G, stop.B))
            .ToArray();
    }
}
