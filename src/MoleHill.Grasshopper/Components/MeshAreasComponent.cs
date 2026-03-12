using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using MoleHill.Core.Engine;
using Rhino.Geometry;
using MoleHill.Core.Grading;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Cut areas from a terrain mesh by closed boundary curves.
/// Each area becomes a separate mesh that can be baked with its own material.
/// </summary>
public class MeshAreasComponent : GH_Component
{
    public MeshAreasComponent()
        : base("Mesh Areas", "Areas",
               "Cut areas from a mesh by closed boundary curves. Each area becomes a separate mesh for independent materials/baking.",
               "MoleHill", "Analysis")
    {
    }

    public override Guid ComponentGuid => new("C4D5E6F7-A8B9-0123-DEF0-234567890123");

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.MeshAreas.png");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Terrain mesh.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Boundaries", "B", "Closed curves defining area boundaries.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Max Area", "A", "Maximum triangle area for mesh refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("Min Angle", "N", "Minimum triangle angle in degrees for mesh refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Area Meshes", "M", "One mesh per boundary area.", GH_ParamAccess.list);
        pManager.AddMeshParameter("Remainder", "R", "Mesh of faces not inside any area.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Face Counts", "F", "Number of faces per area.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Mesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null) return;

        var boundaryCurves = new List<Curve>();
        if (!DA.GetDataList(1, boundaryCurves) || boundaryCurves.Count == 0) return;

        double maxArea = 0.0, minAngle = 0.0;
        DA.GetData(2, ref maxArea);
        DA.GetData(3, ref minAngle);

        double tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        // Extract mesh data
        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        if (faceCount == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh has no faces.");
            return;
        }

        var vertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            var pt = mesh.Vertices[i];
            vertices[i * 3] = pt.X;
            vertices[i * 3 + 1] = pt.Y;
            vertices[i * 3 + 2] = pt.Z;
        }

        var faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            var face = mesh.Faces[i];
            if (face.IsQuad)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mesh contains quad faces. Only triangle meshes are supported.");
                return;
            }
            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }

        // Convert boundary curves
        var areas = new List<MeshAreaSplitter.AreaBoundary>();
        foreach (var crv in boundaryCurves)
        {
            if (crv == null) continue;

            if (!crv.IsClosed)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary curve is not closed. Skipping.");
                continue;
            }

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not tessellate boundary curve. Skipping.");
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
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid boundary curves.");
            return;
        }

        // Split
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
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, errorMessage ?? "Mesh area split failed.");
            return;
        }

        if (errorMessage != null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, errorMessage);

        // Build separate meshes per area
        var areaMeshes = new List<Mesh>();
        var faceCounts = new List<int>();

        for (int a = 0; a < result.AreaCount; a++)
        {
            areaMeshes.Add(BuildSubMesh(result, a));
            faceCounts.Add(CountFaces(result.FaceAreaIndex, result.FaceCount, a));
        }

        var remainder = BuildSubMesh(result, -1);

        DA.SetDataList(0, areaMeshes);
        DA.SetData(1, remainder);
        DA.SetDataList(2, faceCounts);
    }

    /// <summary>
    /// Build a Rhino Mesh from faces matching a specific area index.
    /// </summary>
    private static Mesh BuildSubMesh(MeshAreaSplitter.SplitResult result, int areaIndex)
    {
        // Collect faces for this area
        var faceIndices = new List<int>();
        for (int f = 0; f < result.FaceCount; f++)
        {
            if (result.FaceAreaIndex[f] == areaIndex)
                faceIndices.Add(f);
        }

        // Collect unique vertices used by these faces
        var usedVerts = new HashSet<int>();
        foreach (int f in faceIndices)
        {
            usedVerts.Add(result.Faces[f * 3]);
            usedVerts.Add(result.Faces[f * 3 + 1]);
            usedVerts.Add(result.Faces[f * 3 + 2]);
        }

        // Build vertex remapping
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

    private static int CountFaces(int[] faceAreaIndex, int faceCount, int areaIndex)
    {
        int count = 0;
        for (int i = 0; i < faceCount; i++)
            if (faceAreaIndex[i] == areaIndex) count++;
        return count;
    }
}
