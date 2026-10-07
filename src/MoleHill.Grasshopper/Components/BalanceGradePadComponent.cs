// Searches a bounded pad elevation range for a target net cut/fill volume.
using MoleHill.Core.Engine;
using Grasshopper.Kernel;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public sealed class BalanceGradePadComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public BalanceGradePadComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;
    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.BalanceGradePad.png");
    public override Guid ComponentGuid => new("C137B57B-76DC-4D71-A83B-65C7EC24226F");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Balance Grade Pad",
        Nick = "BalancePad",
        Description = "Search a bounded pad elevation range for a target cut-minus-fill volume. Returns the actual chosen boundary and graded mesh, all samples, and a convergence status.",
        SubCategory = "Grading",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Fixed reference terrain mesh; optional when Terrain is supplied." , optional: true),
            GhPort.Curve("Boundary", "B", "One closed 3D pad boundary; its Z profile is translated together.", access: GH_ParamAccess.item, optional: false),
            GhPort.Number("Minimum Elevation", "Min", "Lowest permitted elevation at the first boundary vertex.", optional: false),
            GhPort.Number("Maximum Elevation", "Max", "Highest permitted elevation at the first boundary vertex.", optional: false),
            GhPort.Number("Target Net Volume", "Nv", "Requested cut minus fill in cubic model units.", @default: 0),
            GhPort.Number("Volume Tolerance", "Tol", "Maximum absolute difference between achieved and target net volume, in cubic model units.", @default: 0.1),
            GhPort.Text("Cut Slope", "Sc", "Cut batter slope; bare numbers are degrees. Also accepts 25%, 150prom, 14deg, 1:3.", access: GH_ParamAccess.item, optional: true),
            GhPort.Text("Fill Slope", "Sf", "Optional fill batter slope; empty inherits cut. Bare numbers are degrees.", access: GH_ParamAccess.item, optional: true),
            GhPort.Number("Max Distance", "D", "Maximum horizontal grading reach in model units; 0 is automatic.", @default: 0),
            GhPort.Integer("Iteration Cap", "I", "Maximum bisection rounds after both endpoint samples.", @default: 24),
            GhPort.Curve("Lock Curves", "L", "Curves whose plan edges remain locked during each candidate grade."),
            GhPort.Generic("Terrain", "T", "Optional typed Terrain input; its metadata is carried to the appended Terrain output.", optional: true),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Best completed graded mesh."),
            GhPort.Curve("Chosen Boundary", "B", "Actual translated 3D input boundary for recreation in Rhino.", access: GH_ParamAccess.item),
            GhPort.Number("Chosen Elevation", "Z", "Elevation of the first chosen boundary vertex."),
            GhPort.Number("Cut Volume", "Cv", "Actual best excavation volume."),
            GhPort.Number("Fill Volume", "Fv", "Actual best embankment volume."),
            GhPort.Number("Net Volume", "Nv", "Actual best cut minus fill."),
            GhPort.Text("Status", "St", "Converged, NoBracket, IterationCap, NonMonotone, or GradingFallback.", access: GH_ParamAccess.item),
            GhPort.Text("Diagnostics", "D", "Search or grading diagnostics."),
            GhPort.Number("Sample Elevations", "Zs", "Elevations measured in solve order.", access: GH_ParamAccess.list),
            GhPort.Number("Sample Cut", "Cs", "Measured excavation at each elevation.", access: GH_ParamAccess.list),
            GhPort.Number("Sample Fill", "Fs", "Measured embankment at each elevation.", access: GH_ParamAccess.list),
            GhPort.Number("Sample Net", "Ns", "Measured cut minus fill at each elevation.", access: GH_ParamAccess.list),
            GhPort.Generic("Terrain", "T", "Terrain-aware balanced output when a Terrain input was supplied."),
        },
        Solve = Solve
    };

    private static void Solve(GhSolveContext ctx)
    {
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(11, out var typedTerrain) ? typedTerrain : null;
        if (!ctx.TryGetMesh(0, out Mesh mesh) && sourceTerrain == null)
            return;
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();
        if (!ctx.TryGetCurve(1, out Curve curve) || !curve.IsClosed)
        {
            ctx.Error("Supply one closed pad boundary curve.");
            return;
        }

        double minimum = ctx.GetNumber(2, double.NaN);
        double maximum = ctx.GetNumber(3, double.NaN);
        double targetNet = ctx.GetNumber(4);
        double volumeTolerance = ctx.GetNumber(5, 0.1);
        double maxDistance = ctx.GetNumber(8);
        int iterationCap = ctx.GetInt(9, 24);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= maximum ||
            !double.IsFinite(volumeTolerance) || volumeTolerance < 0 || iterationCap < 0)
        {
            ctx.Error("Minimum must be below maximum; tolerance and iteration cap must be nonnegative.");
            return;
        }
        if (!TrySlope(ctx.GetText(6, "33deg"), out double cutSlope) ||
            !TrySlope(ctx.GetText(7, ""), out double fillSlope, optional: true))
        {
            ctx.Error("Slope must be an angle or an explicit unit such as 33deg, 25%, or 1:3.");
            return;
        }

        double tolerance = Math.Max(ctx.Tolerance, 1e-8);
        if (!curve.TryGetPolyline(out Polyline polyline))
        {
            PolylineCurve? tessellated = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
            if (tessellated == null || !tessellated.TryGetPolyline(out polyline))
            {
                ctx.Error("Could not tessellate the pad boundary.");
                return;
            }
        }
        int boundaryCount = polyline.Count;
        if (boundaryCount > 1 && polyline[0].DistanceTo(polyline[^1]) <= tolerance)
            boundaryCount--;
        if (!GradePadComponent.TryCreatePadBoundary(
            polyline, boundaryCount, cutSlope, maxDistance,
            cornerFanSegments: 0, stitchApronDistance: ctx.FromMeters(0.5),
            fillSlopeAngle: fillSlope, out PadGrader.PadBoundary? pad, out string? warning))
        {
            ctx.Error(warning ?? "Boundary did not define a stable pad plane.");
            return;
        }
        if (!ctx.TryExtractMesh(mesh, out var extracted))
            return;

        PadGrader.LockCurve[] locks = ConvertLocks(ctx.GetCurves(10), tolerance, out int ignoredLocks);
        if (ignoredLocks > 0)
        {
            ctx.Error($"{ignoredLocks} lock curve(s) could not be converted to polylines. Correct them before balancing.");
            return;
        }
        PadElevationBalanceResult result;
        try
        {
            result = PadElevationBalancer.Balance(new IndexedTriMesh(extracted.Vertices, extracted.VertexCount, extracted.Faces, extracted.FaceCount), pad!, locks, minimum, maximum, targetNet, volumeTolerance, iterationCap, modelTolerance: tolerance);
        }
        catch (ArgumentException exception)
        {
            ctx.Error(exception.Message);
            return;
        }

        VolumeSearchResult search = result.Search;
        ctx.SetData(6, search.Status);
        ctx.SetDataList(7, search.Diagnostic == null ? Array.Empty<string>() : new[] { search.Diagnostic });
        ctx.SetDataList(8, search.Samples.Select(sample => sample.Elevation).ToArray());
        ctx.SetDataList(9, search.Samples.Select(sample => sample.Cut).ToArray());
        ctx.SetDataList(10, search.Samples.Select(sample => sample.Fill).ToArray());
        ctx.SetDataList(11, search.Samples.Select(sample => sample.Net).ToArray());
        if (search.Best is not { } best || result.BestGrading == null || result.AdjustedBoundaryVertices == null)
        {
            ctx.Error(search.Diagnostic ?? "No completed grade was measured.");
            return;
        }

        GradingResult grading = result.BestGrading;
        var points = new Point3d[pad!.VertexCount + 1];
        for (int index = 0; index < pad.VertexCount; index++)
        {
            double[] coordinates = result.AdjustedBoundaryVertices;
            points[index] = new Point3d(coordinates[index * 3], coordinates[index * 3 + 1], coordinates[index * 3 + 2]);
        }
        points[^1] = points[0];
        var outMesh = GhSolveContext.BuildMesh(grading.Vertices, grading.Faces);
        ctx.SetData(0, outMesh);
        ctx.SetData(1, new PolylineCurve(points));
        ctx.SetData(2, best.Elevation);
        ctx.SetData(3, best.Cut);
        ctx.SetData(4, best.Fill);
        ctx.SetData(5, best.Net);
        if (sourceTerrain != null)
        {
            var terrain = new MoleHillTerrainData(outMesh, sourceTerrain.Breaklines, sourceTerrain.Regions,
                sourceTerrain.Name, sourceTerrain.Key, sourceTerrain.Revision, sourceTerrain.Diagnostics,
                sourceTerrain.UnitSystem, sourceTerrain.MetersPerModelUnit, sourceTerrain.LocalToWorld,
                sourceTerrain.HasProjectBaseTransform);
            ctx.SetData(12, new MoleHillTerrainGoo(terrain));
        }
        if (search.Status != "Converged")
            ctx.Warn(search.Diagnostic ?? $"Balance search ended with {search.Status}.");
    }

    private static bool TrySlope(string text, out double degrees, bool optional = false)
    {
        degrees = 0;
        if (optional && string.IsNullOrWhiteSpace(text))
            return true;
        if (!SlopeInput.TryParse(text, SlopeAnalyzer.SlopeUnit.Degrees, out double ratio))
            return false;
        degrees = Math.Atan(ratio) * 180.0 / Math.PI;
        return degrees > 0 && degrees < 90;
    }

    private static PadGrader.LockCurve[] ConvertLocks(IEnumerable<Curve> curves, double tolerance, out int ignored)
    {
        ignored = 0;
        var locks = new List<PadGrader.LockCurve>();
        foreach (Curve curve in curves)
        {
            if (!curve.TryGetPolyline(out Polyline polyline))
            {
                PolylineCurve? tessellated = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (tessellated == null || !tessellated.TryGetPolyline(out polyline))
                {
                    ignored++;
                    continue;
                }
            }
            if (polyline.Count < 2)
            {
                ignored++;
                continue;
            }
            var xy = new double[polyline.Count * 2];
            for (int index = 0; index < polyline.Count; index++)
            {
                xy[index * 2] = polyline[index].X;
                xy[index * 2 + 1] = polyline[index].Y;
            }
            locks.Add(new PadGrader.LockCurve(xy, polyline.Count));
        }
        return locks.ToArray();
    }
}
