using Grasshopper.Kernel;
using Rhino.Geometry;
using MoleHill.Core.Grading;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Laplacian smoothing of mesh vertices, optionally within boundary curves.
/// Breakline curves can be used to hold ridge/valley vertices in place.
/// </summary>
public class MeshSmoothComponent : GH_Component
{
    public MeshSmoothComponent()
        : base("Mesh Smooth", "Smooth",
               "Smooth mesh vertices using Laplacian smoothing. Optionally constrain to boundary regions and fix breakline vertices.",
               "MoleHill", "Surface")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.Smoothing.png");

    public override Guid ComponentGuid => new("F6A7B8C9-D0E1-2345-6789-ABCDEF012345");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Triangle mesh to smooth.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Boundaries", "B", "Closed curves defining regions to smooth. Leave empty to smooth the whole interior.", GH_ParamAccess.list);
        pManager[1].Optional = true;
        pManager.AddIntegerParameter("Iterations", "I", "Number of smoothing passes.", GH_ParamAccess.item, 3);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("Strength", "S", "Smoothing strength (0-1). When boundaries are provided, one value per boundary. When no boundaries, the first value sets global strength (default 0.5).", GH_ParamAccess.list);
        pManager[3].Optional = true;
        pManager.AddCurveParameter("Breaklines", "BL", "Curves along ridges or edges whose vertices should resist smoothing.", GH_ParamAccess.list);
        pManager[4].Optional = true;
        pManager.AddNumberParameter("Fixity", "F", "How fixed breakline vertices are (0 = free, 1 = fully fixed).", GH_ParamAccess.item, 1.0);
        pManager[5].Optional = true;
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
        DA.GetDataList(1, boundaryCurves);

        int iterations = 3;
        DA.GetData(2, ref iterations);

        var strengths = new List<double>();
        DA.GetDataList(3, strengths);

        var breaklineCurves = new List<Curve>();
        DA.GetDataList(4, breaklineCurves);

        double breaklineFixity = 1.0;
        DA.GetData(5, ref breaklineFixity);

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

        if (!mesh.IsValid)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh is already invalid (likely degenerate faces from triangulation). Smoothing will not fix this.");

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

        // Warn only if curves were supplied but none were valid closed curves
        if (boundaryCurves.Count > 0 && boundaries.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No valid boundary curves. Curves must be closed.");
            DA.SetData(0, mesh);
            return;
        }

        // Global strength used when no boundaries are provided
        double globalStrength = strengths.Count > 0
            ? Math.Max(0, Math.Min(1, strengths[0]))
            : 0.5;

        // Convert breakline curves (open or closed)
        var breaklineData = new List<(double[] xyPts, int ptCount)>();
        foreach (var crv in breaklineCurves)
        {
            if (crv == null) continue;

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl)) continue;
            }

            if (pl.Count < 2) continue;

            // Keep the duplicate closing point for closed breaklines so that
            // BuildBreaklineSegments generates the closing segment [n-1 → 0].
            // (Boundaries strip the duplicate because PointInPolygon wraps around
            // implicitly, but breakline segment generation needs the explicit repeat.)
            int plCount = pl.Count;

            var xyPts = new double[plCount * 2];
            for (int i = 0; i < plCount; i++)
            {
                xyPts[i * 2] = pl[i].X;
                xyPts[i * 2 + 1] = pl[i].Y;
            }

            breaklineData.Add((xyPts, plCount));
        }

        // Smooth
        var smoothed = MeshSmoother.Smooth(
            vertices, vertexCount,
            faces, faceCount,
            boundaries.ToArray(),
            globalStrength,
            breaklineData.ToArray(),
            breaklineFixity,
            tolerance,
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
