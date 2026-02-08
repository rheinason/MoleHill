using Grasshopper.Kernel;
using Rhino.Geometry;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Grading;
using System.Drawing;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Visualize area boundaries as colored meshes with priority-based overlap.
/// Two modes:
///   - Without mesh: flat colored areas at curve Z (2D planning)
///   - With mesh: terrain mesh colored by area (3D rendering)
/// Outputs individual area meshes for baking to separate layers.
/// </summary>
public class MeshCollageComponent : GH_Component
{
    public MeshCollageComponent()
        : base("Mesh Collage", "Collage",
               "Visualize area boundaries as colored meshes. Without mesh = flat 2D planning. With mesh = colored 3D terrain. Bake individual areas to layers.",
               "MoleHill", "Analysis")
    {
    }

    public override Guid ComponentGuid => new("E5F6A7B8-C9D0-1234-5678-9ABCDEF01234");

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.MeshCollage.png");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddCurveParameter("Areas", "A", "Closed boundary curves. Curve Z = area elevation. Order = priority (later overrides).", GH_ParamAccess.list);
        pManager.AddColourParameter("Colors", "C", "Color per area. If fewer colors than areas, colors cycle.", GH_ParamAccess.list);
        pManager[1].Optional = true;
        pManager.AddMeshParameter("Mesh", "M", "Optional terrain mesh. If provided, colors the terrain. If empty, creates flat areas at curve Z.", GH_ParamAccess.item);
        pManager[2].Optional = true;
        pManager.AddColourParameter("Base Color", "BC", "Color for uncovered terrain (only used with mesh input).", GH_ParamAccess.item, Color.FromArgb(200, 200, 200));
        pManager[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Colored Mesh", "M", "Combined mesh colored by area.", GH_ParamAccess.item);
        pManager.AddMeshParameter("Area Meshes", "AM", "Individual mesh per area (for baking to separate layers).", GH_ParamAccess.list);
        pManager.AddNumberParameter("Heights", "H", "Target elevation (Z) per area.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Face Counts", "F", "Number of faces per area.", GH_ParamAccess.list);
    }

    private static readonly Color[] DefaultPalette =
    {
        Color.FromArgb(120, 180, 100), // grass green
        Color.FromArgb(180, 170, 150), // stone/pave
        Color.FromArgb(100, 100, 100), // asphalt
        Color.FromArgb(160, 140, 100), // gravel
        Color.FromArgb(80, 130, 80),   // dark green
        Color.FromArgb(200, 180, 140), // sand
        Color.FromArgb(140, 120, 100), // wood
        Color.FromArgb(100, 150, 200), // water
    };

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var areaCurves = new List<Curve>();
        if (!DA.GetDataList(0, areaCurves) || areaCurves.Count == 0) return;

        var userColors = new List<Color>();
        DA.GetDataList(1, userColors);

        Mesh? terrainMesh = null;
        DA.GetData(2, ref terrainMesh);

        Color baseColor = Color.FromArgb(200, 200, 200);
        DA.GetData(3, ref baseColor);

        if (terrainMesh != null && terrainMesh.Faces.Count > 0)
            Solve3D(DA, areaCurves, userColors, terrainMesh, baseColor);
        else
            Solve2D(DA, areaCurves, userColors);
    }

    /// <summary>
    /// 2D planning mode: triangulate each area as a flat surface at curve Z.
    /// </summary>
    private void Solve2D(IGH_DataAccess DA, List<Curve> areaCurves, List<Color> userColors)
    {
        double tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        var areaMeshes = new List<Mesh>();
        var heights = new List<double>();
        var faceCounts = new List<int>();
        var combinedMesh = new Mesh();

        int areaIdx = 0;
        foreach (var crv in areaCurves)
        {
            if (crv == null) { areaIdx++; continue; }
            if (!crv.IsClosed)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Area curve is not closed. Skipping.");
                areaIdx++;
                continue;
            }

            var bbox = crv.GetBoundingBox(false);
            double targetZ = (bbox.Min.Z + bbox.Max.Z) * 0.5;

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not tessellate area curve. Skipping.");
                    areaIdx++;
                    continue;
                }
            }

            if (pl.Count < 3) { areaIdx++; continue; }

            int plCount = pl.Count;
            if (pl[0].DistanceTo(pl[plCount - 1]) < tolerance)
                plCount--;
            if (plCount < 3) { areaIdx++; continue; }

            var areaMesh = TriangulatePolygon(pl, plCount, targetZ);
            if (areaMesh == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Could not triangulate area {areaIdx}. Skipping.");
                areaIdx++;
                continue;
            }

            Color areaColor = GetColor(areaIdx, userColors);

            // Add to combined mesh with vertex colors
            int baseVi = combinedMesh.Vertices.Count;
            for (int i = 0; i < areaMesh.Vertices.Count; i++)
            {
                combinedMesh.Vertices.Add(areaMesh.Vertices[i]);
                combinedMesh.VertexColors.Add(areaColor);
            }
            for (int i = 0; i < areaMesh.Faces.Count; i++)
            {
                var f = areaMesh.Faces[i];
                combinedMesh.Faces.AddFace(f.A + baseVi, f.B + baseVi, f.C + baseVi);
            }

            areaMeshes.Add(areaMesh);
            heights.Add(targetZ);
            faceCounts.Add(areaMesh.Faces.Count);
            areaIdx++;
        }

        if (areaMeshes.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid area curves.");
            return;
        }

        combinedMesh.Normals.ComputeNormals();
        combinedMesh.UnifyNormals();
        combinedMesh.Compact();

        DA.SetData(0, combinedMesh);
        DA.SetDataList(1, areaMeshes);
        DA.SetDataList(2, heights);
        DA.SetDataList(3, faceCounts);
    }

    /// <summary>
    /// 3D rendering mode: color terrain mesh faces by which area they fall in.
    /// </summary>
    private void Solve3D(IGH_DataAccess DA, List<Curve> areaCurves, List<Color> userColors,
                         Mesh terrainMesh, Color baseColor)
    {
        double tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        int vertexCount = terrainMesh.Vertices.Count;
        int faceCount = terrainMesh.Faces.Count;

        var vertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            var pt = terrainMesh.Vertices[i];
            vertices[i * 3] = pt.X;
            vertices[i * 3 + 1] = pt.Y;
            vertices[i * 3 + 2] = pt.Z;
        }

        var faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            var face = terrainMesh.Faces[i];
            if (face.IsQuad)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mesh contains quad faces. Only triangle meshes are supported.");
                return;
            }
            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }

        // Convert curves to boundaries
        var areas = new List<MeshAreaSplitter.AreaBoundary>();
        var heights = new List<double>();

        foreach (var crv in areaCurves)
        {
            if (crv == null) continue;
            if (!crv.IsClosed)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Area curve is not closed. Skipping.");
                continue;
            }

            var bbox = crv.GetBoundingBox(false);
            heights.Add((bbox.Min.Z + bbox.Max.Z) * 0.5);

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not tessellate area curve. Skipping.");
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
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid area curves.");
            return;
        }

        // Split mesh by areas
        var result = MeshAreaSplitter.Split(
            vertices, vertexCount, faces, faceCount,
            areas.ToArray(), 0, 0, out string? errorMessage);

        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, errorMessage ?? "Collage split failed.");
            return;
        }

        if (errorMessage != null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, errorMessage);

        // Build combined colored mesh (flat shaded)
        var coloredMesh = new Mesh();
        coloredMesh.Vertices.Capacity = result.FaceCount * 3;
        coloredMesh.Faces.Capacity = result.FaceCount;

        for (int f = 0; f < result.FaceCount; f++)
        {
            int i0 = result.Faces[f * 3];
            int i1 = result.Faces[f * 3 + 1];
            int i2 = result.Faces[f * 3 + 2];

            int vi = coloredMesh.Vertices.Count;
            coloredMesh.Vertices.Add(result.Vertices[i0 * 3], result.Vertices[i0 * 3 + 1], result.Vertices[i0 * 3 + 2]);
            coloredMesh.Vertices.Add(result.Vertices[i1 * 3], result.Vertices[i1 * 3 + 1], result.Vertices[i1 * 3 + 2]);
            coloredMesh.Vertices.Add(result.Vertices[i2 * 3], result.Vertices[i2 * 3 + 1], result.Vertices[i2 * 3 + 2]);
            coloredMesh.Faces.AddFace(vi, vi + 1, vi + 2);

            int aIdx = result.FaceAreaIndex[f];
            Color faceColor = aIdx >= 0 ? GetColor(aIdx, userColors) : baseColor;
            coloredMesh.VertexColors.Add(faceColor);
            coloredMesh.VertexColors.Add(faceColor);
            coloredMesh.VertexColors.Add(faceColor);
        }

        coloredMesh.Normals.ComputeNormals();
        coloredMesh.UnifyNormals();
        coloredMesh.Compact();

        // Build individual area meshes for baking
        var areaMeshes = new List<Mesh>();
        var faceCounts = new List<int>();

        for (int a = 0; a < result.AreaCount; a++)
        {
            areaMeshes.Add(BuildSubMesh(result, a));
            int count = 0;
            for (int f = 0; f < result.FaceCount; f++)
                if (result.FaceAreaIndex[f] == a) count++;
            faceCounts.Add(count);
        }

        DA.SetData(0, coloredMesh);
        DA.SetDataList(1, areaMeshes);
        DA.SetDataList(2, heights);
        DA.SetDataList(3, faceCounts);
    }

    private Color GetColor(int index, List<Color> userColors)
    {
        if (userColors.Count > 0)
            return userColors[index % userColors.Count];
        return DefaultPalette[index % DefaultPalette.Length];
    }

    private static Mesh BuildSubMesh(MeshAreaSplitter.SplitResult result, int areaIndex)
    {
        var faceIndices = new List<int>();
        for (int f = 0; f < result.FaceCount; f++)
            if (result.FaceAreaIndex[f] == areaIndex)
                faceIndices.Add(f);

        var usedVerts = new HashSet<int>();
        foreach (int f in faceIndices)
        {
            usedVerts.Add(result.Faces[f * 3]);
            usedVerts.Add(result.Faces[f * 3 + 1]);
            usedVerts.Add(result.Faces[f * 3 + 2]);
        }

        var oldToNew = new Dictionary<int, int>();
        var mesh = new Mesh();
        mesh.Vertices.Capacity = usedVerts.Count;
        mesh.Faces.Capacity = faceIndices.Count;

        foreach (int vi in usedVerts)
        {
            oldToNew[vi] = mesh.Vertices.Count;
            mesh.Vertices.Add(
                result.Vertices[vi * 3],
                result.Vertices[vi * 3 + 1],
                result.Vertices[vi * 3 + 2]);
        }

        foreach (int f in faceIndices)
        {
            mesh.Faces.AddFace(
                oldToNew[result.Faces[f * 3]],
                oldToNew[result.Faces[f * 3 + 1]],
                oldToNew[result.Faces[f * 3 + 2]]);
        }

        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        return mesh;
    }

    private static Mesh? TriangulatePolygon(Polyline pl, int vertCount, double targetZ)
    {
        try
        {
            var polygon = new Polygon(vertCount);
            var verts = new Vertex[vertCount];

            for (int i = 0; i < vertCount; i++)
            {
                verts[i] = new Vertex(pl[i].X, pl[i].Y) { ID = i };
                polygon.Add(verts[i]);
            }

            for (int i = 0; i < vertCount; i++)
                polygon.Add(new Segment(verts[i], verts[(i + 1) % vertCount], 1), false);

            var opts = new ConstraintOptions { ConformingDelaunay = false, Convex = false };
            var mesh = new GenericMesher().Triangulate(polygon, opts, null);

            if (mesh.Triangles.Count == 0) return null;

            var outVerts = mesh.Vertices.ToList();
            var outTris = mesh.Triangles.ToList();

            var rhinoMesh = new Mesh();
            rhinoMesh.Vertices.Capacity = outVerts.Count;
            rhinoMesh.Faces.Capacity = outTris.Count;

            var idToIdx = new Dictionary<int, int>(outVerts.Count);
            for (int i = 0; i < outVerts.Count; i++)
            {
                var v = outVerts[i];
                idToIdx[v.ID] = i;
                rhinoMesh.Vertices.Add(v.X, v.Y, targetZ);
            }

            for (int i = 0; i < outTris.Count; i++)
            {
                var tri = outTris[i];
                rhinoMesh.Faces.AddFace(
                    idToIdx.GetValueOrDefault(tri.GetVertex(0).ID, 0),
                    idToIdx.GetValueOrDefault(tri.GetVertex(1).ID, 0),
                    idToIdx.GetValueOrDefault(tri.GetVertex(2).ID, 0));
            }

            rhinoMesh.Normals.ComputeNormals();
            rhinoMesh.UnifyNormals();
            rhinoMesh.Compact();
            return rhinoMesh;
        }
        catch
        {
            return null;
        }
    }
}
