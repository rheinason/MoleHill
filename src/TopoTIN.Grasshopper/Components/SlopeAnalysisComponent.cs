using Grasshopper.Kernel;
using Rhino.Geometry;
using TopoTIN.Core.Analysis;

namespace TopoTIN.Grasshopper.Components;

/// <summary>
/// Color-code a mesh by per-face slope angle/percent.
/// </summary>
public class SlopeAnalysisComponent : GH_Component
{
    public SlopeAnalysisComponent()
        : base("Slope Analysis", "Slope",
               "Color-code a mesh by per-face slope. Green = flat, yellow = moderate, red = steep.",
               "TopoTIN", "Analysis")
    {
    }

    public override Guid ComponentGuid => new("A2B3C4D5-E6F7-8901-BCDE-F12345678901");

    protected override System.Drawing.Bitmap? Icon =>
        TopoTINInfo.LoadIcon("TopoTIN.Grasshopper.Resources.SlopeAnalysis.png");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Terrain mesh to analyze.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Unit", "U", "Slope unit: 0=ratio, 1=percent, 2=degrees.", GH_ParamAccess.item, 1);
        pManager[1].Optional = true;
        pManager.AddNumberParameter("Low", "L", "Low end of color range (green). Default 0.", GH_ParamAccess.item, 0.0);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("High", "H", "High end of color range (red). 0 = auto from data.", GH_ParamAccess.item, 0.0);
        pManager[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Colored Mesh", "M", "Mesh colored by slope.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Slopes", "S", "Per-face slope values.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Min", "Mn", "Minimum slope.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Max", "Mx", "Maximum slope.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Average", "Av", "Area-weighted average slope.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Mesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null) return;

        int unit = 1;
        DA.GetData(1, ref unit);
        if (unit < 0 || unit > 2) unit = 1;

        double colorLow = 0.0, colorHigh = 0.0;
        DA.GetData(2, ref colorLow);
        DA.GetData(3, ref colorHigh);

        // Extract flat arrays from Rhino mesh
        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        if (faceCount == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Mesh has no faces.");
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

        var result = SlopeAnalyzer.Analyze(vertices, vertexCount, faces, faceCount,
                                            (SlopeAnalyzer.SlopeUnit)unit,
                                            colorLow, colorHigh);

        // Build colored mesh with unshared vertices (flat shading)
        var coloredMesh = new Mesh();
        coloredMesh.Vertices.Capacity = faceCount * 3;
        coloredMesh.Faces.Capacity = faceCount;
        coloredMesh.VertexColors.Capacity = faceCount * 3;

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            int vi = f * 3;
            coloredMesh.Vertices.Add(vertices[i0 * 3], vertices[i0 * 3 + 1], vertices[i0 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i1 * 3], vertices[i1 * 3 + 1], vertices[i1 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i2 * 3], vertices[i2 * 3 + 1], vertices[i2 * 3 + 2]);

            coloredMesh.Faces.AddFace(vi, vi + 1, vi + 2);

            byte r = result.FaceColors[f * 3];
            byte g = result.FaceColors[f * 3 + 1];
            byte b = result.FaceColors[f * 3 + 2];

            var color = System.Drawing.Color.FromArgb(r, g, b);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
        }

        coloredMesh.Normals.ComputeNormals();
        coloredMesh.UnifyNormals();
        coloredMesh.Compact();

        DA.SetData(0, coloredMesh);
        DA.SetDataList(1, result.Slopes);
        DA.SetData(2, result.Min);
        DA.SetData(3, result.Max);
        DA.SetData(4, result.Average);
    }
}
