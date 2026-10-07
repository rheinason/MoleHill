using MoleHill.Core.Processing;
using MoleHill.Grasshopper.Registry;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>Blends one 2.5D mesh toward a target mesh inside optional XY boundaries.</summary>
public sealed class ProjectToComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();
    public ProjectToComponent() : base(ComponentSpec) { }
    protected override GhComponentSpec Spec => ComponentSpec;
    protected override System.Drawing.Bitmap? Icon => MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.ProjectTo.png");
    public override Guid ComponentGuid => new("E8F90123-4567-89AB-CDEF-0123456789AB");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Project To",
        Nick = "ProjectTo",
        Description = "Blend a terrain vertically toward a target 2.5D mesh with optional boundaries and feathering.",
        SubCategory = "Surface",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Input triangle terrain mesh. Optional when Terrain is supplied.", optional: true),
            GhPort.Mesh("Target", "T", "Target 2.5D mesh.", optional: false),
            GhPort.Number("Strength", "S", "Projection blend strength from 0 to 1.", optional: true, @default: 1.0),
            GhPort.Number("Feather Distance", "F", "Blend distance inside each boundary in model units.", optional: true, @default: 0.0),
            GhPort.Curve("Boundaries", "B", "Optional closed XY loops; nested loops form holes."),
            GhPort.Generic("Terrain", "Tn", "Optional typed Terrain input; its metadata is carried to the appended Terrain output.", optional: true)
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Projected mesh."),
            GhPort.Integer("Changed Vertices", "C", "Number of vertices whose elevation changed."),
            GhPort.Generic("Terrain", "Tn", "Terrain-aware projected output when a Terrain input was supplied.")
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(5, out var typedTerrain) ? typedTerrain : null;
        bool hasMesh = ctx.TryGetMesh(0, out Mesh input);
        if (!hasMesh && sourceTerrain == null) return;
        input ??= sourceTerrain!.Mesh.DuplicateMesh();
        if (!ctx.TryGetMesh(1, out Mesh target) || !ctx.TryExtractMesh(target, out var targetMesh))
            return;
        if (!ctx.TryExtractMesh(input, out var inputMesh))
            return;

        var loops = new List<double[]>();
        foreach (Curve curve in ctx.GetCurves(4))
        {
            if (curve == null || !curve.IsClosed) { ctx.Warn("Boundaries must be closed curves; skipped one input."); continue; }
            Polyline polyline;
            if (!curve.TryGetPolyline(out polyline))
            {
                Curve? tessellated = curve.ToPolyline(ctx.Tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (tessellated == null || !tessellated.TryGetPolyline(out polyline)) continue;
            }
            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[count - 1]) <= ctx.Tolerance) count--;
            if (count < 3) continue;
            var xy = new double[count * 2];
            for (int i = 0; i < count; i++) { xy[i * 2] = polyline[i].X; xy[i * 2 + 1] = polyline[i].Y; }
            loops.Add(xy);
        }

        double[] vertices = inputMesh.Vertices;
        double[] projected = SurfaceConformer.Conform(inputMesh, targetMesh, loops, ctx.GetNumber(2, 1.0), ctx.GetNumber(3), ctx.Tolerance);
        int changed = 0;
        for (int i = 0; i < vertices.Length; i += 3)
            if (Math.Abs(projected[i + 2] - vertices[i + 2]) > ctx.Tolerance) changed++;
        Mesh output = GhSolveContext.BuildMesh(projected, inputMesh.Faces);
        ctx.SetData(0, output);
        ctx.SetData(1, changed);
        if (sourceTerrain != null)
        {
            var terrain = new MoleHillTerrainData(output, sourceTerrain.Breaklines, sourceTerrain.Regions,
                sourceTerrain.Name, sourceTerrain.Key, sourceTerrain.Revision, sourceTerrain.Diagnostics,
                sourceTerrain.UnitSystem, sourceTerrain.MetersPerModelUnit, sourceTerrain.LocalToWorld,
                sourceTerrain.HasProjectBaseTransform);
            ctx.SetData(2, new MoleHillTerrainGoo(terrain));
        }
    }
}
