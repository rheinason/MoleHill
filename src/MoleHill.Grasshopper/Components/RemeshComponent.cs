using MoleHill.Core.Engine;
using MoleHill.Grasshopper.Registry;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Refine a triangle mesh by re-triangulating with quality constraints. Spec-driven
/// (<see cref="RegistryTerrainComponent"/>): parameter registration + mesh/curve plumbing are shared; only
/// the remesh-specific solve body lives here.
/// </summary>
public sealed class RemeshComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public RemeshComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.Remesh.png");

    public override Guid ComponentGuid => new("D5E6F7A8-B9C0-1234-EF01-345678901234");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Remesh",
        Nick = "Remesh",
        Description = "Refine a triangle mesh with quality constraints. Adds vertices to improve triangle shape and density.",
        SubCategory = "Surface",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Triangle mesh to refine."),
            GhPort.Curve("Constraints", "C", "Curves to preserve as mesh edges (breaklines, boundaries)."),
            GhPort.Number("Edge Length", "E", "Maximum edge length. Controls point density. 0 = no constraint.", @default: 0.0),
            GhPort.Number("Max Area", "A", "Maximum triangle area. 0 = no constraint. Overrides Edge Length if both set.", @default: 0.0),
            GhPort.Number("Min Angle", "N", "Minimum triangle angle in degrees. 0 = no constraint.", @default: 20.0),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Refined mesh."),
            GhPort.Integer("Face Count", "F", "Number of faces."),
            GhPort.Integer("Vertex Count", "V", "Number of vertices."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        if (!ctx.TryGetMesh(0, out var mesh))
            return;

        var constraints = ctx.GetCurves(1);
        double edgeLength = ctx.GetNumber(2, 0.0);
        double maxArea = ctx.GetNumber(3, 0.0);
        double minAngle = ctx.GetNumber(4, 20.0);

        // Convert edge length to max area (equilateral triangle: area = edge^2 * sqrt(3) / 4)
        if (edgeLength > 0 && maxArea <= 0)
            maxArea = edgeLength * edgeLength * Math.Sqrt(3.0) / 4.0;

        double tolerance = ctx.Tolerance;
        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        if (faceCount == 0)
        {
            ctx.Warn("Input mesh has no faces.");
            return;
        }

        if (maxArea <= 0 && minAngle <= 0)
        {
            ctx.Remark("No quality constraints set. Output equals input.");
            ctx.SetData(0, mesh);
            ctx.SetData(1, faceCount);
            ctx.SetData(2, vertexCount);
            return;
        }

        var origVerts = GhSolveContext.ToFlatVertices(mesh);
        if (!ctx.TryToFlatFaces(mesh, out var origFaces))
            return;

        var remeshConstraints = new List<SurfaceRemesher.ConstraintPolyline>();
        foreach (var crv in constraints)
        {
            if (crv == null)
                continue;

            if (!AdaptivePolylineBuilder.TryGetPolyline(crv, tolerance, requireClosed: false, edgeLength, maxArea, out var polyline))
                continue;

            remeshConstraints.Add(ToConstraintPolyline(polyline, crv.IsClosed));
        }

        var remeshResult = SurfaceRemesher.Remesh(
            origVerts,
            origFaces,
            remeshConstraints,
            new SurfaceRemesher.Options
            {
                Tolerance = tolerance,
                RequestedEdgeLength = edgeLength,
                MaxArea = maxArea,
                MinAngle = minAngle,
                ProtectSharpEdges = true,
            });

        if (!remeshResult.Success)
        {
            ctx.Warn(remeshResult.Warning ?? "Remesh could not preserve the mesh boundary or supplied constraints. Output equals input mesh.");
            ctx.SetData(0, mesh);
            ctx.SetData(1, faceCount);
            ctx.SetData(2, vertexCount);
            return;
        }

        if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            ctx.Warn(remeshResult.Warning);

        var outMesh = GhSolveContext.BuildMesh(remeshResult.Vertices, remeshResult.Faces);
        ctx.SetData(0, outMesh);
        ctx.SetData(1, remeshResult.Faces.Length / 3);
        ctx.SetData(2, remeshResult.Vertices.Length / 3);
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
}
