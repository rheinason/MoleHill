using Grasshopper.Kernel;
using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using MoleHill.Grasshopper.Registry;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>Field-guided quad retopology. This is a deliberate terminal Mesh route because its output is quad-dominant.</summary>
public sealed class RetopoComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public RetopoComponent() : base(ComponentSpec) { }
    protected override GhComponentSpec Spec => ComponentSpec;
    protected override System.Drawing.Bitmap? Icon => MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.Retopo.png");
    public override Guid ComponentGuid => new("B8C9D0E1-F234-5678-9ABC-DEF012345678");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Retopo", Nick = "Retopo",
        Description = "Generate a field-guided quad-dominant mesh. Terminal output; it does not produce typed Terrain.",
        SubCategory = "Surface",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Triangle mesh to retopologize."),
            GhPort.Curve("Constraints", "C", "Breaklines and boundaries to preserve."),
            GhPort.Number("Edge Length", "E", "Target quad edge length. 0 = automatic."),
            GhPort.Number("Crease Angle", "A", "Crease detection angle in degrees.", @default: 30.0),
            GhPort.Number("Wall Face Slope", "W", "Slope threshold for frozen wall faces in degrees.", @default: 70.0),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Quad-dominant retopologized mesh (terminal)."),
            GhPort.Integer("Quad Count", "Q", "Number of generated quads."),
            GhPort.Integer("Triangle Count", "T", "Number of remaining triangles."),
            GhPort.Text("Warning", "W", "Retopology warning text."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        if (!ctx.TryGetMesh(0, out var mesh)) return;
        if (!ctx.TryToFlatFaces(mesh, out var faces)) return;
        if (mesh.Faces.Count == 0) { ctx.Error("Input mesh has no faces."); return; }

        var vertices = GhSolveContext.ToFlatVertices(mesh);
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>();
        foreach (var curve in ctx.GetCurves(1))
        {
            if (curve == null || !curve.TryGetPolyline(out var polyline) || polyline.Count < 2) continue;
            var points = new double[polyline.Count * 3];
            for (int i = 0; i < polyline.Count; i++)
            {
                points[i * 3] = polyline[i].X; points[i * 3 + 1] = polyline[i].Y; points[i * 3 + 2] = polyline[i].Z;
            }
            constraints.Add(new SurfaceRemesher.ConstraintPolyline(points, polyline.Count, curve.IsClosed));
        }

        var result = QuadRemesher.Remesh(vertices, faces, constraints, new QuadRemesher.Options
        {
            EdgeLength = ctx.GetNumber(2, 0.0),
            CreaseAngleDeg = ctx.GetNumber(3, 30.0),
            WallFaceMinSlopeDeg = ctx.GetNumber(4, 70.0),
            Tolerance = ctx.Tolerance,
            Iterations = 5,
        });
        if (!result.Success)
        {
            ctx.Warn(result.Warning ?? "Retopology failed. Output equals input mesh.");
            ctx.SetData(0, mesh); ctx.SetData(1, 0); ctx.SetData(2, mesh.Faces.Count);
            ctx.SetData(3, result.Warning ?? "Retopology failed.");
            return;
        }

        var output = new Mesh();
        for (int i = 0; i < result.Vertices.Length; i += 3)
            output.Vertices.Add(result.Vertices[i], result.Vertices[i + 1], result.Vertices[i + 2]);
        for (int i = 0; i < result.Quads.Length; i += 4)
            output.Faces.AddFace(result.Quads[i], result.Quads[i + 1], result.Quads[i + 2], result.Quads[i + 3]);
        for (int i = 0; i < result.Tris.Length; i += 3)
            output.Faces.AddFace(result.Tris[i], result.Tris[i + 1], result.Tris[i + 2]);
        output.Normals.ComputeNormals(); output.UnifyNormals(); output.Compact();
        ctx.SetData(0, output); ctx.SetData(1, result.QuadCount); ctx.SetData(2, result.TriangleCount);
        ctx.SetData(3, result.Warning ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(result.Warning)) ctx.Warn(result.Warning);
    }
}
