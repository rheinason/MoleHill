using MoleHill.Core.Engine;
using Grasshopper.Kernel;
using Rhino.Geometry;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using MoleHill.Grasshopper.Types;
using MoleHill.Shared;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Grade terrain along path curves (roads, sidewalks, etc.) with controlled width and slope transitions.
/// Multiple paths with per-path settings via matching-length lists. Spec-driven
/// (<see cref="RegistryTerrainComponent"/>).
/// </summary>
public sealed class GradePathComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public GradePathComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.GradePath.png");

    public override Guid ComponentGuid => new("A7B8C9D0-E1F2-3456-789A-BCDEF0123456");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Grade Path",
        Nick = "GradePath",
        Description = "Grade terrain along path curves with specified width and slope transitions. Curve Z = road elevation.",
        SubCategory = "Grading",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Existing terrain mesh. Optional when Terrain is supplied.", optional: true),
            GhPort.Curve("Paths", "P", "Path curves. Curve Z = road elevation profile.", optional: false),
            GhPort.Number("Width", "W", "Road width per path (total, centered on path). Shorter lists repeat last value.", access: GH_ParamAccess.list, optional: false),
            GhPort.Number("Slope Angle", "S", "Cut slope angle in degrees per path (terrain above the road). Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Number("Max Distance", "D", "Max horizontal transition distance per path. 0 = auto. Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Number("Fill Slope", "Sf", "Fill slope angle in degrees per path (terrain below the road). 0 = same as cut slope. Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Curve("Width Edges", "E", "Optional plan curves matched uniquely to centerline sides. Edge Z is ignored.", access: GH_ParamAccess.list),
            GhPort.Number("Max Edge Distance", "Ed", "Maximum edge matching distance. 0 = four times fallback Width.", optional: true),
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
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(8, out var typedTerrain) ? typedTerrain : null;
        if (!ctx.TryGetMesh(0, out var mesh) && sourceTerrain == null)
            return;
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();

        var pathCurves = ctx.GetCurves(1);
        if (pathCurves.Count == 0)
            return;

        var widths = ctx.GetNumbers(2);
        if (widths.Count == 0)
        {
            ctx.Error("Width is required.");
            return;
        }

        var slopeAngles = ctx.GetNumbers(3);
        var maxDists = ctx.GetNumbers(4);
        var fillSlopeAngles = ctx.GetNumbers(5);

        double tolerance = ctx.Tolerance;
        int faceCount = mesh.Faces.Count;
        if (faceCount == 0)
        {
            ctx.Warn("Input mesh has no faces.");
            return;
        }

        if (!ctx.TryExtractMesh(mesh, out var extracted))
            return;
        double[] vertices = extracted.Vertices;
        int vertexCount = extracted.VertexCount;
        int[] faces = extracted.Faces;
        faceCount = extracted.FaceCount;

        // Convert path curves to PathDefinitions
        var pathDefs = new List<PathGrader.PathDefinition>();
        int pathIdx = 0;
        foreach (var crv in pathCurves)
        {
            if (crv == null) { pathIdx++; continue; }

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    ctx.Warn($"Could not tessellate path curve {pathIdx}. Skipping.");
                    pathIdx++;
                    continue;
                }
            }

            if (pl.Count < 2)
            {
                ctx.Warn($"Path {pathIdx} must have at least 2 points. Skipping.");
                pathIdx++;
                continue;
            }

            var pathXy = new double[pl.Count * 2];
            var pathZ = new double[pl.Count];
            for (int i = 0; i < pl.Count; i++)
            {
                pathXy[i * 2] = pl[i].X;
                pathXy[i * 2 + 1] = pl[i].Y;
                pathZ[i] = pl[i].Z;
            }

            double w = GhSolveContext.ListValue(widths, pathIdx, widths[widths.Count - 1]);
            double slope = GhSolveContext.ListValue(slopeAngles, pathIdx, 33.0);
            double dist = GhSolveContext.ListValue(maxDists, pathIdx, 0.0);
            double fillSlope = GhSolveContext.ListValue(fillSlopeAngles, pathIdx, 0.0);

            if (w <= 0)
            {
                ctx.Warn($"Path {pathIdx} width must be positive. Skipping.");
                pathIdx++;
                continue;
            }

            pathDefs.Add(new PathGrader.PathDefinition(
                pathXy,
                pathZ,
                pl.Count,
                w,
                slope,
                dist,
                fillSlope,
                isClosed: crv.IsClosed));
            pathIdx++;
        }

        if (pathDefs.Count == 0)
        {
            ctx.Error("No valid path curves.");
            return;
        }

        var edgeDefs = new List<VariablePathWidthResolver.EdgeDefinition>();
        var edgeCurves = ctx.GetCurves(6);
        for (int edgeIndex = 0; edgeIndex < edgeCurves.Count; edgeIndex++)
        {
            Curve? edgeCurve = edgeCurves[edgeIndex];
            if (edgeCurve == null)
                continue;
            if (!TryToPlanPolyline(edgeCurve, tolerance, out Polyline edgePolyline))
            {
                ctx.Warn($"Could not tessellate width edge {edgeIndex}. Skipping.");
                continue;
            }

            var edgeXy = new double[edgePolyline.Count * 2];
            for (int i = 0; i < edgePolyline.Count; i++)
            {
                edgeXy[i * 2] = edgePolyline[i].X;
                edgeXy[(i * 2) + 1] = edgePolyline[i].Y;
            }
            edgeDefs.Add(new VariablePathWidthResolver.EdgeDefinition(edgeXy, edgePolyline.Count, edgeCurve.IsClosed, edgeIndex));
        }

        double maxEdgeDistance = ctx.GetNumbers(7).FirstOrDefault();
        VariablePathWidthResolver.Result widthResult = VariablePathWidthResolver.Resolve(
            pathDefs,
            edgeDefs,
            new VariablePathWidthResolver.Options
            {
                MaxEdgeDistance = maxEdgeDistance,
                Tolerance = tolerance
            });
        foreach (VariablePathWidthResolver.Diagnostic diagnostic in widthResult.Diagnostics)
        {
            // A clean match is confirmation, not a problem — only unmatched/ambiguous/partial edges warn.
            if (diagnostic.Code == "grade_path.variable_edge.matched")
                ctx.Remark(diagnostic.Message);
            else
                ctx.Warn(diagnostic.Message);
        }

        GradeOutcome gradeOutcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            Paths = widthResult.Paths,
        });
        string? errorMessage = gradeOutcome.ErrorMessage;
        var result = gradeOutcome.Result;

        if (result == null)
        {
            ctx.Error(errorMessage ?? "Path grading failed.");
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

    private static bool TryToPlanPolyline(Curve curve, double tolerance, out Polyline polyline)
    {
        if (curve.TryGetPolyline(out polyline) && polyline.Count >= 2)
            return true;
        PolylineCurve? polylineCurve = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
        return polylineCurve != null && polylineCurve.TryGetPolyline(out polyline) && polyline.Count >= 2;
    }
}
