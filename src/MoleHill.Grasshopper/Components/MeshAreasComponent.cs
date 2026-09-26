using Grasshopper.Kernel;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Cut areas from a terrain mesh by closed boundary curves. Each area becomes a separate mesh that can be
/// baked with its own material. Spec-driven (<see cref="RegistryTerrainComponent"/>).
/// </summary>
public sealed class MeshAreasComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public MeshAreasComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.MeshAreas.png");

    public override Guid ComponentGuid => new("C4D5E6F7-A8B9-0123-DEF0-234567890123");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Mesh Areas",
        Nick = "Areas",
        Description = "Cut areas from a mesh by closed boundary curves. Each area becomes a separate mesh for independent materials/baking.",
        SubCategory = "Analysis",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Terrain mesh."),
            GhPort.Curve("Boundaries", "B", "Closed curves defining area boundaries.", optional: false),
            GhPort.Number("Max Area", "A", "Maximum triangle area for mesh refinement. 0 = no constraint.", @default: 0.0),
            GhPort.Number("Min Angle", "N", "Minimum triangle angle in degrees for mesh refinement. 0 = no constraint.", @default: 0.0),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Area Meshes", "M", "One mesh per boundary area.", access: GH_ParamAccess.list),
            GhPort.Mesh("Remainder", "R", "Mesh of faces not inside any area."),
            GhPort.Integer("Face Counts", "F", "Number of faces per area.", access: GH_ParamAccess.list),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        if (!ctx.TryGetMesh(0, out var mesh))
            return;

        var boundaryCurves = ctx.GetCurves(1);
        if (boundaryCurves.Count == 0)
            return;

        double maxArea = ctx.GetNumber(2, 0.0);
        double minAngle = ctx.GetNumber(3, 0.0);

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

        // Convert boundary curves
        var areas = new List<MeshAreaSplitter.AreaBoundary>();
        foreach (var crv in boundaryCurves)
        {
            if (crv == null) continue;

            if (!crv.IsClosed)
            {
                ctx.Warn("Boundary curve is not closed. Skipping.");
                continue;
            }

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    ctx.Warn("Could not tessellate boundary curve. Skipping.");
                    continue;
                }
            }

            if (pl.Count < 3) continue;

            int plCount = pl.Count;
            if (pl[0].DistanceTo(pl[plCount - 1]) < tolerance)
                plCount--;

            var xyVerts = new double[plCount * 2];
            for (int i = 0; i < plCount; i++)
            {
                xyVerts[i * 2] = pl[i].X;
                xyVerts[i * 2 + 1] = pl[i].Y;
            }

            areas.Add(new MeshAreaSplitter.AreaBoundary(xyVerts, plCount));
        }

        if (areas.Count == 0)
        {
            ctx.Error("No valid boundary curves.");
            return;
        }

        var result = MeshAreaSplitter.Split(
            vertices, vertexCount,
            faces, faceCount,
            areas.ToArray(),
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            tolerance,
            maxArea, minAngle,
            out string? errorMessage);

        if (result == null)
        {
            ctx.Error(errorMessage ?? "Mesh area split failed.");
            return;
        }

        if (errorMessage != null)
            ctx.Warn(errorMessage);

        var areaMeshes = new List<Mesh>();
        var faceCounts = new List<int>();

        // Group once: finding each area's faces by rescanning the whole result is O(areas x faces).
        FaceOwnerGroups groups = FaceOwnerGroups.Build(result.FaceAreaIndex, result.FaceCount, result.AreaCount);
        var remap = new SubMeshVertexRemap(result.VertexCount);

        for (int a = 0; a < result.AreaCount; a++)
        {
            ReadOnlySpan<int> areaFaces = groups.Faces(a);
            areaMeshes.Add(BuildSubMesh(result, areaFaces, remap));
            faceCounts.Add(areaFaces.Length);
        }

        var remainder = BuildSubMesh(result, groups.Faces(FaceOwnerGroups.RemainderOwner), remap);

        ctx.SetDataList(0, areaMeshes);
        ctx.SetData(1, remainder);
        ctx.SetDataList(2, faceCounts);
    }

    /// <summary>Build a Rhino Mesh from an already-grouped run of face indices.</summary>
    private static Mesh BuildSubMesh(
        MeshAreaSplitter.SplitResult result,
        ReadOnlySpan<int> faceIndices,
        SubMeshVertexRemap remap)
    {
        var mesh = new Mesh();
        mesh.Faces.Capacity = faceIndices.Length;
        remap.Begin();

        foreach (int f in faceIndices)
        {
            int a = MapVertex(result, remap, mesh, result.Faces[f * 3]);
            int b = MapVertex(result, remap, mesh, result.Faces[f * 3 + 1]);
            int c = MapVertex(result, remap, mesh, result.Faces[f * 3 + 2]);
            mesh.Faces.AddFace(a, b, c);
        }

        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        return mesh;
    }

    private static int MapVertex(MeshAreaSplitter.SplitResult result, SubMeshVertexRemap remap, Mesh mesh, int vertexIndex)
    {
        if (remap.TryGet(vertexIndex, out int existing))
            return existing;

        int newIndex = mesh.Vertices.Count;
        mesh.Vertices.Add(
            result.Vertices[vertexIndex * 3],
            result.Vertices[vertexIndex * 3 + 1],
            result.Vertices[vertexIndex * 3 + 2]);
        remap.Set(vertexIndex, newIndex);
        return newIndex;
    }
}
