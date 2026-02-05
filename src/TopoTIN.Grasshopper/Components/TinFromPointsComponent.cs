using Grasshopper.Kernel;
using Rhino.Geometry;
using TopoTIN.Core.Engine;
using TopoTIN.Core.Processing;
using TopoTIN.Grasshopper.Utilities;

namespace TopoTIN.Grasshopper.Components;

/// <summary>
/// Simplified TIN component: points only, no breaklines.
/// </summary>
public class TinFromPointsComponent : GH_Component
{
    private TinEngine _engine = new();

    public TinFromPointsComponent()
        : base("TIN Points", "TINp",
               "Generate a TIN surface from points using Delaunay triangulation.",
               "Mesh", "Triangulation")
    {
    }

    public override Guid ComponentGuid => new("E2A2B3C4-D5E6-7890-ABCD-EF1234567890");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddPointParameter("Points", "P", "Survey points for TIN generation.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Max Area", "A", "Maximum triangle area for refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[1].Optional = true;
        pManager.AddNumberParameter("Min Angle", "N", "Minimum triangle angle in degrees for refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Triangulated mesh.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Face Count", "F", "Number of triangular faces.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Vertex Count", "V", "Number of vertices.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var points = new List<Point3d>();
        if (!DA.GetDataList(0, points) || points.Count < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least 3 points are required.");
            return;
        }

        double maxArea = 0, minAngle = 0;
        DA.GetData(1, ref maxArea);
        DA.GetData(2, ref minAngle);

        // Build flat arrays
        var spotXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        // Process (no breaklines)
        var emptyBreaklines = BreaklineDiscretizer.Process(Array.Empty<double[]>());
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count, emptyBreaklines);

        if (merged.DuplicatesRemoved > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{merged.DuplicatesRemoved} duplicate points were merged.");
        if (merged.InvalidsSkipped > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{merged.InvalidsSkipped} invalid points (NaN/Infinity) were skipped.");

        if (merged.VertexCount < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Fewer than 3 unique points after deduplication.");
            return;
        }

        var quality = new QualitySettings { MaxArea = maxArea, MinAngle = minAngle };
        var result = _engine.Build(merged.XyCoords, merged.ZValues, merged.Segments, quality);

        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Triangulation failed. Points may be collinear.");
            return;
        }

        var mesh = RhinoConverter.ToRhinoMesh(result);

        DA.SetData(0, mesh);
        DA.SetData(1, result.FaceCount);
        DA.SetData(2, result.VertexCount);
    }
}
