using Grasshopper.Kernel;
using Rhino.Geometry;
using TopoTIN.Core.Grading;

namespace TopoTIN.Grasshopper.Components;

/// <summary>
/// Grade terrain along path curves (roads, sidewalks, etc.)
/// with controlled width and slope transitions.
/// Multiple paths with per-path settings via matching-length lists.
/// </summary>
public class GradePathComponent : GH_Component
{
    public GradePathComponent()
        : base("Grade Path", "GradePath",
               "Grade terrain along path curves with specified width and slope transitions. Curve Z = road elevation.",
               "TopoTIN", "Grading")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        TopoTINInfo.LoadIcon("TopoTIN.Grasshopper.Resources.GradePath.png");

    public override Guid ComponentGuid => new("A7B8C9D0-E1F2-3456-789A-BCDEF0123456");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Existing terrain mesh.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Paths", "P", "Path curves. Curve Z = road elevation profile.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Width", "W", "Road width per path (total, centered on path). Shorter lists repeat last value.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Slope Angle", "S", "Transition slope angle in degrees per path. Shorter lists repeat last value.", GH_ParamAccess.list);
        pManager[3].Optional = true;
        pManager.AddNumberParameter("Max Distance", "D", "Max horizontal transition distance per path. 0 = auto. Shorter lists repeat last value.", GH_ParamAccess.list);
        pManager[4].Optional = true;
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

        var pathCurves = new List<Curve>();
        if (!DA.GetDataList(1, pathCurves) || pathCurves.Count == 0) return;

        var widths = new List<double>();
        if (!DA.GetDataList(2, widths) || widths.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Width is required.");
            return;
        }

        var slopeAngles = new List<double>();
        var maxDists = new List<double>();
        DA.GetDataList(3, slopeAngles);
        DA.GetDataList(4, maxDists);

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

        // Convert path curves to PathDefinitions
        var pathDefs = new List<PathGrader.PathDefinition>();
        int pathIdx = 0;
        foreach (var crv in pathCurves)
        {
            if (crv == null) { pathIdx++; continue; }

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Could not tessellate path curve {pathIdx}. Skipping.");
                    pathIdx++;
                    continue;
                }
            }

            if (pl.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Path {pathIdx} must have at least 2 points. Skipping.");
                pathIdx++;
                continue;
            }

            var pathXy = new double[pl.Count * 2];
            var pathZ = new double[pl.Count];
            for (int i = 0; i < pl.Count; i++)
            {
                pathXy[i * 2] = pl[i].X;
                pathXy[i * 2 + 1] = pl[i].Y;
                pathZ[i] = pl[i].Z;
            }

            double w = GetListValue(widths, pathIdx, widths[widths.Count - 1]);
            double slope = GetListValue(slopeAngles, pathIdx, 33.0);
            double dist = GetListValue(maxDists, pathIdx, 0.0);

            if (w <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Path {pathIdx} width must be positive. Skipping.");
                pathIdx++;
                continue;
            }

            pathDefs.Add(new PathGrader.PathDefinition(pathXy, pathZ, pl.Count, w, slope, dist));
            pathIdx++;
        }

        if (pathDefs.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid path curves.");
            return;
        }

        // Grade
        var result = PathGrader.Grade(
            vertices, vertexCount,
            faces, faceCount,
            pathDefs.ToArray(),
            out string? errorMessage);

        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, errorMessage ?? "Path grading failed.");
            return;
        }

        if (errorMessage != null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, errorMessage);

        // Build output mesh
        var outMesh = new Mesh();
        outMesh.Vertices.Capacity = result.VertexCount;
        outMesh.Faces.Capacity = result.FaceCount;

        for (int i = 0; i < result.VertexCount; i++)
            outMesh.Vertices.Add(result.Vertices[i * 3], result.Vertices[i * 3 + 1], result.Vertices[i * 3 + 2]);

        for (int i = 0; i < result.FaceCount; i++)
            outMesh.Faces.AddFace(result.Faces[i * 3], result.Faces[i * 3 + 1], result.Faces[i * 3 + 2]);

        outMesh.Normals.ComputeNormals();
        outMesh.UnifyNormals();
        outMesh.Compact();

        DA.SetData(0, outMesh);
        DA.SetData(1, result.CutVolume);
        DA.SetData(2, result.FillVolume);
        DA.SetData(3, result.NetVolume);
    }
}
