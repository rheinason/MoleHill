using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using MoleHill.Shared;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Generate retaining wall solids from curve pairs and insert their rails as hard terrain breaklines.
/// Spec-driven (<see cref="RegistryTerrainComponent"/>).
/// </summary>
public sealed class RetainingWallComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public RetainingWallComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.RetainingWall.png");

    public override Guid ComponentGuid => new("D4E3F8A1-8B13-4CC1-8E56-5390F6D63C4D");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Retaining Wall",
        Nick = "RetainWall",
        Description = "Generate retaining wall solids from curve pairs and insert their rails as hard terrain breaklines.",
        SubCategory = "Grading",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Terrain mesh (triangles only). Optional when Terrain is supplied.", optional: true),
            GhPort.Curve("Wall Curves", "C", "Unordered open or closed 3D curves defining retaining walls.", optional: false),
            GhPort.Number("Max Wall Width", "W", "Maximum expected spacing between paired wall rails. Defaults to 1 m in document units."),
            GhPort.Generic("Terrain", "T", "Optional typed Terrain input; its metadata is carried to the appended Terrain output.", optional: true),
            // Appended after Terrain on purpose: saved definitions bind ports by index, so the four
            // that already existed must keep the positions they have.
            GhPort.Boolean("Grade Terrain", "G", "False (default): insert the wall rails as breaklines only. True: also batter the terrain away from each rail out to daylight. Rail elevations are authoritative either way.", optional: true),
            GhPort.Number("Fill Slope", "S", "Fill slope angle in degrees for the graded mode (terrain below a rail).", optional: true),
            GhPort.Number("Cut Slope", "Sc", "Cut slope angle in degrees (terrain above a rail). 0 = same as fill slope.", optional: true),
            GhPort.Number("Toe Slope", "St", "Toe-side slope override in degrees, applied below the wall. 0 = inherit.", optional: true),
            GhPort.Number("Top Slope", "Sp", "Top-side slope override in degrees, applied above the wall. 0 = inherit.", optional: true),
            GhPort.Number("Max Distance", "D", "Maximum grading reach away from a rail. 0 = unlimited.", optional: true),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Terrain mesh with accepted wall rails inserted as hard breaklines."),
            GhPort.Brep("Wall Breps", "W", "Solid retaining wall Breps."),
            GhPort.Line("Pairs", "P", "Preview lines connecting matched pairs."),
            GhPort.Text("Report", "R", "Info / warning / error report entries."),
            GhPort.Generic("Terrain", "T", "Terrain-aware retaining wall output when a Terrain input was supplied."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(3, out var typedTerrain) ? typedTerrain : null;
        if (!ctx.TryGetMesh(0, out var mesh) && sourceTerrain == null)
            return;
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();

        var wallCurves = ctx.GetCurves(1);
        if (wallCurves.Count == 0)
        {
            ctx.Warn("No wall curves provided.");
            ctx.SetData(0, mesh);
            ctx.SetDataList(1, Array.Empty<Brep>());
            ctx.SetDataList(2, Array.Empty<Line>());
            ctx.SetDataList(3, new[] { "[Warning] No wall curves provided." });
            EmitTerrain(ctx, sourceTerrain, mesh);
            return;
        }

        double maxWallWidth = ctx.GetNumber(2, ctx.FromMeters(1.0));
        if (maxWallWidth <= 0.0)
        {
            ctx.Error("Max wall width must be > 0.");
            return;
        }

        if (!ctx.TryExtractMesh(mesh, out var extracted))
            return;
        double[] vertices = extracted.Vertices;
        int[] faces = extracted.Faces;

        double modelTolerance = ctx.Tolerance;
        var plan = RetainingWallPlannerCore.Plan(
            wallCurves,
            Math.Max(modelTolerance, maxWallWidth),
            curveParsingTolerance: modelTolerance);
        var reportOut = new List<string>(plan.Report.Count + 2);
        foreach (var entry in plan.Report)
        {
            reportOut.Add(entry.ToString());
            switch (entry.Level)
            {
                case RetainingWallPlannerCore.ReportLevel.Error:
                    ctx.Error(entry.Message);
                    break;
                case RetainingWallPlannerCore.ReportLevel.Warning:
                    ctx.Warn(entry.Message);
                    break;
                default:
                    ctx.Remark(entry.Message);
                    break;
            }
        }

        if (plan.Walls.Count == 0)
        {
            ctx.SetData(0, mesh);
            ctx.SetDataList(1, Array.Empty<Brep>());
            ctx.SetDataList(2, plan.PairLines);
            ctx.SetDataList(3, reportOut);
            EmitTerrain(ctx, sourceTerrain, mesh);
            return;
        }

        var constraints = new List<ConstraintPolyline>(plan.Walls.Count * 2);
        var wallBreps = new List<Brep>(plan.Walls.Count);
        foreach (var wall in plan.Walls)
        {
            if (wall.Brep != null)
                wallBreps.Add(wall.Brep);

            AddWallConstraints(constraints, wall.Rails);
        }

        SurfaceRemesher.Result remeshResult = SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = modelTolerance,
                RequestedEdgeLength = 0.0,
                MaxArea = 0.0,
                MinAngle = 0.0,
                ProtectSharpEdges = true,
                ConstraintInsertionOnly = true
            });

        Mesh outMesh = mesh;
        if (remeshResult.Success)
        {
            outMesh = GhSolveContext.BuildMesh(remeshResult.Vertices, remeshResult.Faces);
            if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            {
                ctx.Warn(remeshResult.Warning);
                reportOut.Add($"[Warning] {remeshResult.Warning}");
            }
        }
        else
        {
            string warning = remeshResult.Warning ?? "Retaining wall breakline remesh failed. Output mesh equals input mesh.";
            ctx.Warn(warning);
            reportOut.Add($"[Warning] {warning}");
        }

        outMesh = ApplyWallGrading(ctx, outMesh, plan.Walls, modelTolerance, reportOut);

        ctx.SetData(0, outMesh);
        ctx.SetDataList(1, wallBreps);
        ctx.SetDataList(2, plan.PairLines);
        ctx.SetDataList(3, reportOut);
        EmitTerrain(ctx, sourceTerrain, outMesh, plan.Walls.SelectMany(w => new[] { w.Rails.ToePoints, w.Rails.TopPoints }));
    }

    /// <summary>
    /// The graded mode. The rails are already in the mesh as breaklines with their elevations forced,
    /// so this only adds the batters that run away from them. Off by default, so an existing definition
    /// keeps the mesh it produced before this input existed.
    /// </summary>
    private static Mesh ApplyWallGrading(
        GhSolveContext ctx,
        Mesh mesh,
        IReadOnlyList<RetainingWallPlannerCore.PlannedWall> walls,
        double modelTolerance,
        List<string> reportOut)
    {
        if (!ctx.GetBool(4) || walls.Count == 0)
            return mesh;

        double fillSlope = FirstOrDefaultNumber(ctx, 5, 33.0);
        var options = new RetainingWallGradePlanner.Options
        {
            FillAngleDeg = fillSlope,
            CutAngleDeg = FirstOrDefaultNumber(ctx, 6, 0.0),
            Toe = new RetainingWallGradePlanner.SideSlopes(FirstOrDefaultNumber(ctx, 7, 0.0), 0.0),
            Top = new RetainingWallGradePlanner.SideSlopes(FirstOrDefaultNumber(ctx, 8, 0.0), 0.0),
            MaxDistance = FirstOrDefaultNumber(ctx, 9, 0.0),
            Tolerance = modelTolerance,
        };

        List<PathGrader.PathDefinition> railGrades = RetainingWallGradePlanner.Build(walls, options);
        if (railGrades.Count == 0)
            return mesh;

        if (!ctx.TryExtractMesh(mesh, out var gradeTerrain))
            return mesh;

        GradeOutcome gradeOutcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = gradeTerrain,
            Paths = railGrades.ToArray(),
        });
        string? errorMessage = gradeOutcome.ErrorMessage;
        GradingResult? result = gradeOutcome.Result;

        if (result == null)
        {
            string warning = errorMessage ?? "Retaining wall grading failed; the wall breaklines were kept.";
            ctx.Warn(warning);
            reportOut.Add($"[Warning] {warning}");
            return mesh;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            ctx.Warn(errorMessage);
            reportOut.Add($"[Warning] {errorMessage}");
        }

        return GhSolveContext.BuildMesh(result.Vertices, result.Faces);
    }

    private static double FirstOrDefaultNumber(GhSolveContext ctx, int index, double fallback)
    {
        var values = ctx.GetNumbers(index);
        return values.Count > 0 ? values[0] : fallback;
    }

    private static void EmitTerrain(
        GhSolveContext ctx,
        MoleHillTerrainData? source,
        Mesh mesh,
        IEnumerable<Point3d[]>? rails = null)
    {
        if (source == null) return;
        var breaklines = source.Breaklines.Select(c => c.DuplicateCurve()).ToList();
        if (rails != null)
        {
            foreach (var points in rails)
            {
                if (points.Length < 2) continue;
                var polyline = new Polyline(points);
                if (polyline.Count >= 2)
                    breaklines.Add(polyline.ToNurbsCurve());
            }
        }
        var terrain = new MoleHillTerrainData(mesh, breaklines, source.Regions,
            source.Name, source.Key, source.Revision, source.Diagnostics, source.UnitSystem,
            source.MetersPerModelUnit, source.LocalToWorld, source.HasProjectBaseTransform);
        ctx.SetData(4, new MoleHillTerrainGoo(terrain));
    }

    private static void AddWallConstraints(List<ConstraintPolyline> constraints, RetainingWallPlannerCore.WallRails rails)
    {
        int minimum = rails.IsClosed ? 3 : 2;
        if (rails.ToePoints.Length >= minimum)
            constraints.Add(BuildConstraint(rails.ToePoints, rails.IsClosed));
        if (rails.TopPoints.Length >= minimum)
            constraints.Add(BuildConstraint(rails.TopPoints, rails.IsClosed));
    }

    private static ConstraintPolyline BuildConstraint(Point3d[] railPoints, bool isClosed)
    {
        int count = railPoints.Length;
        var points = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            points[i * 3] = railPoints[i].X;
            points[i * 3 + 1] = railPoints[i].Y;
            points[i * 3 + 2] = railPoints[i].Z;
        }

        return new ConstraintPolyline(points, count, isClosed, PreserveInputElevation: true);
    }
}
