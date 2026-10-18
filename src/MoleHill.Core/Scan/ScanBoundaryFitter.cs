using MoleHill.Core.Analysis;
using MoleHill.Core.Geometry;
using MoleHill.Core.Processing;
using MoleHill.Core.Routing;

namespace MoleHill.Core.Scan;

/// <summary>Removes raster steps in plan before sampling the clean ground's height.</summary>
internal static class ScanBoundaryFitter
{
    public static FittedRouteCurve Fit(double[] raw, bool closed, ScanGrid ground,
        MeshHeightProjector? projector, double planTolerance, double verticalTolerance,
        CancellationToken cancellationToken = default)
    {
        // Height must not hold a grid corner in place: moving it sideways on a slope changes its Z.
        // Spend the plan budget in three parts: thinning, plan fitting, then fitting the draped profile.
        double[] flat = (double[])raw.Clone();
        for (int i = 2; i < flat.Length; i += 3)
            flat[i] = 0;
        if (closed && flat.Length >= 6 && flat[0] == flat[^3] && flat[1] == flat[^2])
            flat = flat[..^3];
        double[] light = closed
            ? TerrainCurveRecovery.SimplifyClosed(flat, planTolerance * .65, double.PositiveInfinity)
            : TerrainCurveRecovery.SimplifyOpen(flat, planTolerance * .65, double.PositiveInfinity);
        if (closed)
        {
            // A narrow island can collapse or self-intersect when thinned. Keep a simple outline.
            double tolerance = planTolerance * .65;
            while ((light.Length < 9 || Intersects(light)) && tolerance > planTolerance * .01)
            {
                tolerance *= .5;
                light = TerrainCurveRecovery.SimplifyClosed(flat, tolerance, double.PositiveInfinity);
            }
            if (light.Length < 9 || Intersects(light))
                light = flat;
            light = light.Concat(light.Take(3)).ToArray();
        }
        FittedRouteCurve plan = RouteCurveFitter.Fit(new IReadOnlyList<double>[] { light },
            planTolerance * .25, verticalTolerance, 0, 0, 0);
        double[] outline = plan.PointsXyz;
        if (closed && Intersects(outline[..^3]))
            outline = light;
        var draped = new List<double>();
        for (int i = 0; i + 5 < outline.Length; i += 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double dx = outline[i + 3] - outline[i], dy = outline[i + 4] - outline[i + 1];
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / ground.CellSize));
            for (int j = 0; j < steps; j++)
                Add(outline[i] + dx * j / steps, outline[i + 1] + dy * j / steps);
        }
        if (outline.Length >= 3)
            Add(outline[^3], outline[^2]);
        cancellationToken.ThrowIfCancellationRequested();
        FittedRouteCurve result = RouteCurveFitter.Fit(new IReadOnlyList<double>[] { draped },
            planTolerance * .1, verticalTolerance, 0, 0, 0);
        if (!closed || result.PointsXyz.Length < 6 || !Intersects(result.PointsXyz[..^3]))
            return result;
        // Preserve the simple draped plan when smoothing a very narrow loop would cross itself.
        var segments = new List<IReadOnlyList<double>>();
        for (int i = 0; i + 5 < draped.Count; i += 3)
            segments.Add(draped.Skip(i).Take(6).ToArray());
        return RouteCurveFitter.Fit(segments, planTolerance * .1, verticalTolerance, 0, 0, 0);

        void Add(double x, double y)
        {
            if (!(projector != null && projector.TryGetHeightExtremes(x, y, ground.CellSize * 1e-7, out _, out double z)))
            {
                // Raster cell corners at the domain edge extend half a cell beyond its node centres.
                double cx = Math.Clamp(x, ground.OriginX, ground.NodeX(ground.Columns - 1));
                double cy = Math.Clamp(y, ground.OriginY, ground.NodeY(ground.Rows - 1));
                if (!ScanGridSampling.TryHeight(ground, cx, cy, out z))
                    return;
            }
            draped.Add(x); draped.Add(y); draped.Add(z);
        }
    }

    private static bool Intersects(double[] xyz)
    {
        double[] xy = xyz.Where((_, i) => i % 3 != 2).ToArray();
        return Geometry2D.ClosedPolylineSelfIntersects(xy, xy.Length / 2);
    }
}
