using Grasshopper.Kernel;
using Rhino.Geometry;
using TopoTIN.Core.Grading;

namespace TopoTIN.Grasshopper.Components;

/// <summary>
/// Flatten terrain to a target elevation within boundary curves,
/// with controlled slope transitions. Reports cut/fill volumes.
/// Per-pad settings via matching-length lists.
/// </summary>
public class GradePadComponent : GH_Component
{
    public GradePadComponent()
        : base("Grade Pad", "GradePad",
               "Flatten terrain within boundary curves to a target elevation with slope transitions. Curve Z = target elevation.",
               "TopoTIN", "Grading")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        TopoTINInfo.LoadIcon("TopoTIN.Grasshopper.Resources.GradePad.png");

    public override Guid ComponentGuid => new("B3C4D5E6-F7A8-9012-CDEF-123456789012");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Existing terrain mesh.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Boundaries", "B", "Closed curves defining pad areas. Curve Z = target elevation.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Slope Angle", "S", "Transition slope angle in degrees per boundary. Shorter lists repeat last value.", GH_ParamAccess.list);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("Max Distance", "D", "Max horizontal transition distance per boundary. 0 = auto. Shorter lists repeat last value.", GH_ParamAccess.list);
        pManager[3].Optional = true;
        pManager.AddCurveParameter("Lock Curves", "L", "Curves whose edges are preserved as constrained segments in the remesh.", GH_ParamAccess.list);
        pManager[4].Optional = true;
        pManager.AddNumberParameter("Max Area", "A", "Maximum triangle area for mesh refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[5].Optional = true;
        pManager.AddNumberParameter("Min Angle", "N", "Minimum triangle angle in degrees for mesh refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[6].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Modified terrain mesh.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Cut Volume", "Cv", "Total excavation volume.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Fill Volume", "Fv", "Total embankment volume.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Net Volume", "Nv", "Cut - Fill (positive = net cut).", GH_ParamAccess.item);
    }

    private static T GetListValue<T>(List<T> list, int index, T defaultVal)
    {
        if (list.Count == 0) return defaultVal;
        return list[Math.Min(index, list.Count - 1)];
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Mesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null) return;

        var boundaryCurves = new List<Curve>();
        if (!DA.GetDataList(1, boundaryCurves) || boundaryCurves.Count == 0) return;

        var slopeAngles = new List<double>();
        var maxDists = new List<double>();
        DA.GetDataList(2, slopeAngles);
        DA.GetDataList(3, maxDists);

        var lockCurves = new List<Curve>();
        DA.GetDataList(4, lockCurves);

        double maxArea = 0.0, minAngle = 0.0;
        DA.GetData(5, ref maxArea);
        DA.GetData(6, ref minAngle);

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

        // Convert boundary curves with per-pad settings
        var pads = new List<PadGrader.PadBoundary>();
        int padIdx = 0;
        foreach (var crv in boundaryCurves)
        {
            if (crv == null) { padIdx++; continue; }

            if (!crv.IsClosed)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary curve is not closed. Skipping.");
                padIdx++;
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
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not tessellate boundary curve. Skipping.");
                    padIdx++;
                    continue;
                }
            }

            if (pl.Count < 3) { padIdx++; continue; }

            int plCount = pl.Count;
            if (pl[0].DistanceTo(pl[plCount - 1]) < tolerance)
                plCount--;

            var xyVerts = new double[plCount * 2];
            for (int i = 0; i < plCount; i++)
            {
                xyVerts[i * 2] = pl[i].X;
                xyVerts[i * 2 + 1] = pl[i].Y;
            }

            double slope = GetListValue(slopeAngles, padIdx, 33.0);
            double dist = GetListValue(maxDists, padIdx, 0.0);
            pads.Add(new PadGrader.PadBoundary(xyVerts, plCount, targetZ, slope, dist));
            padIdx++;
        }

        if (pads.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid boundary curves.");
            return;
        }

        // Convert lock curves
        PadGrader.LockCurve[]? locks = null;
        if (lockCurves.Count > 0)
        {
            var lockList = new List<PadGrader.LockCurve>();
            foreach (var crv in lockCurves)
            {
                if (crv == null) continue;

                Polyline pl;
                if (!crv.TryGetPolyline(out pl))
                {
                    var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                    if (polyCrv == null || !polyCrv.TryGetPolyline(out pl)) continue;
                }

                if (pl.Count < 2) continue;

                var xyVerts = new double[pl.Count * 2];
                for (int i = 0; i < pl.Count; i++)
                {
                    xyVerts[i * 2] = pl[i].X;
                    xyVerts[i * 2 + 1] = pl[i].Y;
                }
                lockList.Add(new PadGrader.LockCurve(xyVerts, pl.Count));
            }
            if (lockList.Count > 0) locks = lockList.ToArray();
        }

        // Grade
        var result = PadGrader.Grade(
            vertices, vertexCount,
            faces, faceCount,
            pads.ToArray(),
            locks,
            maxArea, minAngle,
            out string? errorMessage);

        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, errorMessage ?? "Grading failed.");
            return;
        }

        if (errorMessage != null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, errorMessage);

        // Build output mesh
        var outMesh = new Mesh();
        outMesh.Vertices.Capacity = result.VertexCount;
        outMesh.Faces.Capacity = result.FaceCount;

        for (int i = 0; i < result.VertexCount; i++)
        {
            outMesh.Vertices.Add(
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
        }

        for (int i = 0; i < result.FaceCount; i++)
        {
            outMesh.Faces.AddFace(
                result.Faces[i * 3],
                result.Faces[i * 3 + 1],
                result.Faces[i * 3 + 2]);
        }

        outMesh.Normals.ComputeNormals();
        outMesh.UnifyNormals();
        outMesh.Compact();

        DA.SetData(0, outMesh);
        DA.SetData(1, result.CutVolume);
        DA.SetData(2, result.FillVolume);
        DA.SetData(3, result.NetVolume);
    }
}
