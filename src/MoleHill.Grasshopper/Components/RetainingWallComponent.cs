using MoleHill.Core.Engine;
using MoleHill.Grasshopper.Registry;
using MoleHill.Shared;
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
            GhPort.Mesh("Mesh", "M", "Terrain mesh (triangles only)."),
            GhPort.Curve("Wall Curves", "C", "Unordered open or closed 3D curves defining retaining walls.", optional: false),
            GhPort.Number("Max Wall Width", "W", "Maximum expected spacing between paired wall rails.", @default: 1.0),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Terrain mesh with accepted wall rails inserted as hard breaklines."),
            GhPort.Brep("Wall Breps", "W", "Solid retaining wall Breps."),
            GhPort.Line("Pairs", "P", "Preview lines connecting matched pairs."),
            GhPort.Text("Report", "R", "Info / warning / error report entries."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        if (!ctx.TryGetMesh(0, out var mesh))
            return;

        var wallCurves = ctx.GetCurves(1);
        if (wallCurves.Count == 0)
        {
            ctx.Warn("No wall curves provided.");
            ctx.SetData(0, mesh);
            ctx.SetDataList(1, Array.Empty<Brep>());
            ctx.SetDataList(2, Array.Empty<Line>());
            ctx.SetDataList(3, new[] { "[Warning] No wall curves provided." });
            return;
        }

        double maxWallWidth = ctx.GetNumber(2, 1.0);
        if (maxWallWidth <= 0.0)
        {
            ctx.Error("Max wall width must be > 0.");
            return;
        }

        if (!TryExtractTriangleMesh(mesh, out double[] vertices, out int[] faces, out string? meshError))
        {
            ctx.Error(meshError ?? "Could not extract a triangle mesh.");
            return;
        }

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
            return;
        }

        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(plan.Walls.Count * 2);
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

        ctx.SetData(0, outMesh);
        ctx.SetDataList(1, wallBreps);
        ctx.SetDataList(2, plan.PairLines);
        ctx.SetDataList(3, reportOut);
    }

    private static bool TryExtractTriangleMesh(Mesh mesh, out double[] vertices, out int[] faces, out string? error)
    {
        error = null;
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();
        if (mesh.Faces.Count == 0)
        {
            error = "Input mesh has no faces.";
            return false;
        }

        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;
        vertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i * 3] = mesh.Vertices[i].X;
            vertices[i * 3 + 1] = mesh.Vertices[i].Y;
            vertices[i * 3 + 2] = mesh.Vertices[i].Z;
        }

        faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            MeshFace face = mesh.Faces[i];
            if (face.IsQuad)
            {
                error = "Mesh contains quad faces. Only triangle meshes are supported.";
                return false;
            }

            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }

        return true;
    }

    private static void AddWallConstraints(List<SurfaceRemesher.ConstraintPolyline> constraints, RetainingWallPlannerCore.WallRails rails)
    {
        int minimum = rails.IsClosed ? 3 : 2;
        if (rails.ToePoints.Length >= minimum)
            constraints.Add(BuildConstraint(rails.ToePoints, rails.IsClosed));
        if (rails.TopPoints.Length >= minimum)
            constraints.Add(BuildConstraint(rails.TopPoints, rails.IsClosed));
    }

    private static SurfaceRemesher.ConstraintPolyline BuildConstraint(Point3d[] railPoints, bool isClosed)
    {
        int count = railPoints.Length;
        var points = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            points[i * 3] = railPoints[i].X;
            points[i * 3 + 1] = railPoints[i].Y;
            points[i * 3 + 2] = railPoints[i].Z;
        }

        return new SurfaceRemesher.ConstraintPolyline(points, count, isClosed, PreserveInputElevation: true);
    }
}
