using Grasshopper.Kernel;
using Rhino.Geometry;
using MoleHill.Core.Grading;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Laplacian smoothing of mesh vertices within boundary curves.
/// Per-boundary strength via matching-length lists.
/// </summary>
public class MeshSmoothComponent : GH_Component
{
    public MeshSmoothComponent()
        : base("Mesh Smooth", "Smooth",
               "Smooth mesh vertices within boundary curves using Laplacian smoothing.",
               "MoleHill", "Surface")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.Smoothing.png");

    public override Guid ComponentGuid => new("F6A7B8C9-D0E1-2345-6789-ABCDEF012345");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Triangle mesh to smooth.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Boundaries", "B", "Closed curves defining regions to smooth.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Iterations", "I", "Number of smoothing passes.", GH_ParamAccess.item, 3);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("Strength", "S", "Smoothing strength per boundary (0-1). Shorter lists repeat last value.", GH_ParamAccess.list);
        pManager[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Smoothed mesh.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Mesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null) return;

        var boundaryCurves = new List<Curve>();
        if (!DA.GetDataList(1, boundaryCurves) || boundaryCurves.Count == 0) return;

        int iterations = 3;
        DA.GetData(2, ref iterations);

        var strengths = new List<double>();
        DA.GetDataList(3, strengths);

        if (iterations <= 0)
        {
            DA.SetData(0, mesh);
            return;
        }

        double tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        if (faceCount == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh has no faces.");
            return;
        }

        // Extract mesh data
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

        // Convert boundary curves with per-boundary strength
        var boundaries = new List<(double[] xyVerts, int vertCount, double strength)>();
        int bIdx = 0;
        foreach (var crv in boundaryCurves)
        {
            if (crv == null || !crv.IsClosed) { bIdx++; continue; }

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl)) { bIdx++; continue; }
            }

            if (pl.Count < 3) { bIdx++; continue; }

            int plCount = pl.Count;
            if (pl[0].DistanceTo(pl[plCount - 1]) < tolerance)
                plCount--;

            var xyVerts = new double[plCount * 2];
            for (int i = 0; i < plCount; i++)
            {
                xyVerts[i * 2] = pl[i].X;
                xyVerts[i * 2 + 1] = pl[i].Y;
            }

            double s = strengths.Count > 0
                ? strengths[Math.Min(bIdx, strengths.Count - 1)]
                : 0.5;

            boundaries.Add((xyVerts, plCount, s));
            bIdx++;
        }

        if (boundaries.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No valid boundary curves.");
            DA.SetData(0, mesh);
            return;
        }

        // Smooth
        var smoothed = MeshSmoother.Smooth(
            vertices, vertexCount,
            faces, faceCount,
            boundaries.ToArray(),
            iterations);

        // Build output mesh
        var outMesh = new Mesh();
        outMesh.Vertices.Capacity = vertexCount;
        outMesh.Faces.Capacity = faceCount;

        for (int i = 0; i < vertexCount; i++)
            outMesh.Vertices.Add(smoothed[i * 3], smoothed[i * 3 + 1], smoothed[i * 3 + 2]);

        for (int i = 0; i < faceCount; i++)
            outMesh.Faces.AddFace(faces[i * 3], faces[i * 3 + 1], faces[i * 3 + 2]);

        outMesh.Normals.ComputeNormals();
        outMesh.UnifyNormals();
        outMesh.Compact();

        DA.SetData(0, outMesh);
    }
}
