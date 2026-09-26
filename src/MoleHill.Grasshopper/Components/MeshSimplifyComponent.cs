using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Grasshopper.Registry;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>Reduces a triangle terrain while preserving optional mandatory edge segments.</summary>
public sealed class MeshSimplifyComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public MeshSimplifyComponent() : base(ComponentSpec) { }

    protected override GhComponentSpec Spec => ComponentSpec;
    protected override System.Drawing.Bitmap? Icon => MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.Simplify.png");
    public override Guid ComponentGuid => new("D7E8F901-2345-6789-ABCD-EF0123456789");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Mesh Simplify",
        Nick = "Simplify",
        Description = "Reduce a 2.5D triangle terrain by deviation or target vertex count while retaining mandatory edge segments.",
        SubCategory = "Surface",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Triangle terrain mesh. Optional when a Terrain is supplied.", optional: true),
            GhPort.Text("Mode", "Mo", "Maximum deviation or Target vertex count.", optional: true),
            GhPort.Number("Maximum Deviation", "D", "Maximum allowed vertical deviation in model units.", optional: true, @default: 0.0),
            GhPort.Integer("Target Vertex Count", "N", "Target vertex count when Mode is Target vertex count.", optional: true, @default: 0),
            GhPort.Curve("Required Edges", "E", "Curves whose endpoints coincide with mesh vertices and must survive simplification."),
            GhPort.Number("Retain Percentage", "P", "Percentage of used vertices to retain when Mode is Retain percentage.", optional: true, @default: 50.0),
            GhPort.Generic("Terrain", "T", "Optional typed Terrain input; its metadata is carried to the appended Terrain output.", optional: true)
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Simplified triangle mesh."),
            GhPort.Number("Maximum Deviation", "D", "Measured maximum deviation from the input."),
            GhPort.Integer("Protected Vertices", "P", "Number of vertices protected by mandatory edges."),
            GhPort.Text("Status", "S", "Simplifier termination status and diagnostic."),
            GhPort.Generic("Terrain", "T", "Terrain-aware simplified output when a Terrain input was supplied.")
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(6, out var typedTerrain) ? typedTerrain : null;
        bool hasMesh = ctx.TryGetMesh(0, out Mesh mesh);
        if (!hasMesh && sourceTerrain == null)
        {
            ctx.Warn("Supply a Mesh or a Terrain.");
            return;
        }
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();
        if (!ctx.TryToFlatFaces(mesh, out int[] faces)) return;
        if (faces.Length == 0)
        {
            ctx.Error("Input mesh has no triangular faces.");
            return;
        }

        string modeText = ctx.GetText(1, "Maximum deviation");
        bool countMode = modeText.Contains("count", StringComparison.OrdinalIgnoreCase);
        double deviation = Math.Max(0.0, ctx.GetNumber(2));
        int target = ctx.GetInt(3);
        double retainPercentage = ctx.GetNumber(5, 50.0);
        double[] vertices = GhSolveContext.ToFlatVertices(mesh);
        var required = new List<int>();
        foreach (Curve curve in ctx.GetCurves(4))
        {
            if (curve == null || !curve.IsLinear())
            {
                ctx.Warn("Required Edges must be straight curves whose endpoints are mesh vertices.");
                continue;
            }
            int a = NearestVertex(vertices, curve.PointAtStart, ctx.Tolerance);
            int b = NearestVertex(vertices, curve.PointAtEnd, ctx.Tolerance);
            if (a < 0 || b < 0 || a == b)
            {
                ctx.Warn("A Required Edge endpoint does not coincide with a mesh vertex; it was skipped.");
                continue;
            }
            required.Add(a); required.Add(b);
        }

        // A Terrain's breaklines are its hard edges, so they survive simplification wherever the mesh still
        // carries them. One that no longer lies on mesh edges (after an upstream remesh or grade) is skipped
        // rather than failing the whole solve, and is left off the output Terrain so it never claims an edge
        // the simplified mesh does not have.
        IReadOnlyList<Curve> keptBreaklines = Array.Empty<Curve>();
        if (sourceTerrain != null && sourceTerrain.Breaklines.Count > 0)
        {
            var candidates = new List<Curve>();
            var constraints = new List<SurfaceRemesher.ConstraintPolyline>();
            foreach (Curve breakline in sourceTerrain.Breaklines)
            {
                if (breakline == null || !breakline.TryGetPolyline(out Polyline polyline) || polyline.Count < 2)
                    continue;
                candidates.Add(breakline);
                constraints.Add(ToConstraintPolyline(polyline, breakline.IsClosed));
            }

            required.AddRange(SurfaceConstraintEdgeResolver.ResolveEach(
                vertices, faces, constraints, ctx.Tolerance, out bool[] resolved));
            keptBreaklines = candidates.Where((_, i) => resolved[i]).ToArray();
            int skipped = sourceTerrain.Breaklines.Count - keptBreaklines.Count;
            if (skipped > 0)
                ctx.Warn($"{skipped} Terrain breakline(s) do not lie on mesh edges; they were not protected and are left off the Terrain output.");
        }

        int usedVertexCount = faces.Distinct().Count();
        if (modeText.Contains("percentage", StringComparison.OrdinalIgnoreCase))
        {
            // Same resolution as the Rhino Simplify modifier (TerrainBuildService.TryResolvePercentageTarget),
            // so a percentage keeps the same vertex count in both hosts.
            if (!double.IsFinite(retainPercentage) || retainPercentage < 0.0 || retainPercentage > 100.0)
            {
                ctx.Error("Retain Percentage must be between 0 and 100.");
                return;
            }
            target = (int)Math.Floor(usedVertexCount * retainPercentage / 100.0);
            countMode = true;
        }
        var options = new SurfaceSimplifier.Options
        {
            Mode = countMode ? SurfaceSimplifier.SimplificationMode.TargetVertexCount : SurfaceSimplifier.SimplificationMode.MaximumDeviation,
            MaximumDeviation = deviation,
            TargetVertexCount = target,
            NumericalTolerance = Math.Max(ctx.Tolerance * 1e-3, 1e-8)
        };
        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(vertices, faces, required.ToArray(), options);
        Mesh output = GhSolveContext.BuildMesh(result.Vertices, result.Faces);
        ctx.SetData(0, output);
        ctx.SetData(1, result.MaximumDeviation);
        ctx.SetData(2, result.ProtectedVertexCount);
        ctx.SetData(3, $"{result.Termination}: {result.Diagnostic}");
        if (sourceTerrain != null)
        {
            var terrain = new MoleHillTerrainData(output, keptBreaklines, sourceTerrain.Regions,
                sourceTerrain.Name, sourceTerrain.Key, sourceTerrain.Revision, sourceTerrain.Diagnostics,
                sourceTerrain.UnitSystem, sourceTerrain.MetersPerModelUnit, sourceTerrain.LocalToWorld,
                sourceTerrain.HasProjectBaseTransform);
            ctx.SetData(4, new MoleHillTerrainGoo(terrain));
        }
    }

    private static SurfaceRemesher.ConstraintPolyline ToConstraintPolyline(Polyline polyline, bool isClosed)
    {
        var points = new double[polyline.Count * 3];
        for (int i = 0; i < polyline.Count; i++)
        {
            points[i * 3] = polyline[i].X;
            points[i * 3 + 1] = polyline[i].Y;
            points[i * 3 + 2] = polyline[i].Z;
        }

        return new SurfaceRemesher.ConstraintPolyline(points, polyline.Count, isClosed);
    }

    private static int NearestVertex(double[] vertices, Point3d point, double tolerance)
    {
        int nearest = -1;
        double best = tolerance * tolerance;
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double dx = vertices[i * 3] - point.X, dy = vertices[i * 3 + 1] - point.Y, dz = vertices[i * 3 + 2] - point.Z;
            double distance = dx * dx + dy * dy + dz * dz;
            if (distance <= best) { best = distance; nearest = i; }
        }
        return nearest;
    }
}
