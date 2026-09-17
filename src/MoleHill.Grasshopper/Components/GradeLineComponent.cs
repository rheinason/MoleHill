using Grasshopper.Kernel;
using Rhino.Geometry;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using MoleHill.Grasshopper.Types;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Grade terrain away from a drawn line. The curve's Z is the finished elevation and the batters run
/// out to daylight on both sides — the corridor grader at width zero, so a crest, a toe or a swale
/// invert needs no invented width.
/// </summary>
/// <remarks>
/// The four per-side inputs are what make an asymmetric section possible; leave them at zero and both
/// sides use the shared cut/fill pair. There is no per-side "off": a line at an authored elevation is a
/// discontinuity, so every side resolves to some slope. Where the terrain already meets the line, the
/// batter measures no difference and nothing moves.
/// </remarks>
public sealed class GradeLineComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public GradeLineComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.GradeLine.png");

    public override Guid ComponentGuid => new("B2C4D6E8-1A3B-4C5D-9E7F-2A4B6C8D0E1F");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Grade Line",
        Nick = "GradeLine",
        Description = "Grade terrain away from design lines. Curve Z = finished elevation along the line.",
        SubCategory = "Grading",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Existing terrain mesh. Optional when Terrain is supplied.", optional: true),
            GhPort.Curve("Lines", "L", "Design lines. Curve Z = finished elevation along the line.", optional: false),
            GhPort.Number("Fill Slope", "S", "Fill slope angle in degrees per line (terrain below the line). Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Number("Cut Slope", "Sc", "Cut slope angle in degrees per line (terrain above the line). 0 = same as fill slope.", access: GH_ParamAccess.list),
            GhPort.Number("Max Distance", "D", "Max horizontal grading reach per line. 0 = unlimited.", access: GH_ParamAccess.list),
            GhPort.Number("Left Cut", "Lc", "Left-side cut slope override in degrees. 0 = inherit.", access: GH_ParamAccess.list, optional: true),
            GhPort.Number("Left Fill", "Lf", "Left-side fill slope override in degrees. 0 = inherit.", access: GH_ParamAccess.list, optional: true),
            GhPort.Number("Right Cut", "Rc", "Right-side cut slope override in degrees. 0 = inherit.", access: GH_ParamAccess.list, optional: true),
            GhPort.Number("Right Fill", "Rf", "Right-side fill slope override in degrees. 0 = inherit.", access: GH_ParamAccess.list, optional: true),
            GhPort.Generic("Terrain", "T", "Optional typed Terrain input; its metadata is carried to the appended Terrain output.", optional: true),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Modified terrain mesh."),
            GhPort.Number("Cut Volume", "Cv", "Total excavation volume."),
            GhPort.Number("Fill Volume", "Fv", "Total embankment volume."),
            GhPort.Number("Net Volume", "Nv", "Cut - Fill (positive = net cut)."),
            GhPort.Generic("Terrain", "T", "Terrain-aware graded output when a Terrain input was supplied."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(9, out var typedTerrain) ? typedTerrain : null;
        if (!ctx.TryGetMesh(0, out var mesh) && sourceTerrain == null)
            return;
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();

        var lineCurves = ctx.GetCurves(1);
        if (lineCurves.Count == 0)
            return;

        var fillSlopes = ctx.GetNumbers(2);
        var cutSlopes = ctx.GetNumbers(3);
        var maxDists = ctx.GetNumbers(4);
        var leftCuts = ctx.GetNumbers(5);
        var leftFills = ctx.GetNumbers(6);
        var rightCuts = ctx.GetNumbers(7);
        var rightFills = ctx.GetNumbers(8);

        double tolerance = ctx.Tolerance;
        int faceCount = mesh.Faces.Count;
        if (faceCount == 0)
        {
            ctx.Warn("Input mesh has no faces.");
            return;
        }

        var vertices = GhSolveContext.ToFlatVertices(mesh);
        int vertexCount = mesh.Vertices.Count;
        if (!ctx.TryToFlatFaces(mesh, out var faces))
            return;

        var lineDefs = new List<PathGrader.PathDefinition>();
        int lineIdx = 0;
        foreach (var crv in lineCurves)
        {
            if (crv == null) { lineIdx++; continue; }

            if (!TryToPolyline(crv, tolerance, out Polyline pl))
            {
                ctx.Warn($"Could not tessellate design line {lineIdx}. Skipping.");
                lineIdx++;
                continue;
            }

            var xy = new double[pl.Count * 2];
            var z = new double[pl.Count];
            for (int i = 0; i < pl.Count; i++)
            {
                xy[i * 2] = pl[i].X;
                xy[(i * 2) + 1] = pl[i].Y;
                z[i] = pl[i].Z;
            }

            double fillSlope = GhSolveContext.ListValue(fillSlopes, lineIdx, 33.0);
            double cutSlope = GhSolveContext.ListValue(cutSlopes, lineIdx, 0.0);
            double dist = GhSolveContext.ListValue(maxDists, lineIdx, 0.0);

            lineDefs.Add(new PathGrader.PathDefinition(
                xy,
                z,
                pl.Count,
                width: 0.0,
                slopeAngleDeg: cutSlope > 0.0 ? cutSlope : fillSlope,
                maxDistance: dist,
                fillSlopeAngleDeg: fillSlope,
                isClosed: crv.IsClosed,
                leftCutSlopeAngleDeg: GhSolveContext.ListValue(leftCuts, lineIdx, 0.0),
                leftFillSlopeAngleDeg: GhSolveContext.ListValue(leftFills, lineIdx, 0.0),
                rightCutSlopeAngleDeg: GhSolveContext.ListValue(rightCuts, lineIdx, 0.0),
                rightFillSlopeAngleDeg: GhSolveContext.ListValue(rightFills, lineIdx, 0.0)));
            lineIdx++;
        }

        if (lineDefs.Count == 0)
        {
            ctx.Error("No valid design lines.");
            return;
        }

        var result = PathGrader.Grade(
            vertices, vertexCount,
            faces, faceCount,
            lineDefs.ToArray(),
            out string? errorMessage);

        if (result == null)
        {
            ctx.Error(errorMessage ?? "Line grading failed.");
            return;
        }

        if (errorMessage != null)
            ctx.Warn(errorMessage);

        var outMesh = GhSolveContext.BuildMesh(result.Vertices, result.Faces);
        ctx.SetData(0, outMesh);
        ctx.SetData(1, result.CutVolume);
        ctx.SetData(2, result.FillVolume);
        ctx.SetData(3, result.NetVolume);
        if (sourceTerrain != null)
        {
            var terrain = new MoleHillTerrainData(outMesh,
                sourceTerrain.Breaklines, sourceTerrain.Regions, sourceTerrain.Name, sourceTerrain.Key,
                sourceTerrain.Revision, sourceTerrain.Diagnostics, sourceTerrain.UnitSystem,
                sourceTerrain.MetersPerModelUnit, sourceTerrain.LocalToWorld, sourceTerrain.HasProjectBaseTransform);
            ctx.SetData(4, new MoleHillTerrainGoo(terrain));
        }
    }

    private static bool TryToPolyline(Curve curve, double tolerance, out Polyline polyline)
    {
        if (curve.TryGetPolyline(out polyline) && polyline.Count >= 2)
            return true;
        PolylineCurve? polylineCurve = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
        return polylineCurve != null && polylineCurve.TryGetPolyline(out polyline) && polyline.Count >= 2;
    }
}
