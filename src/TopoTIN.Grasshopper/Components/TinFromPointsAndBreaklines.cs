using Grasshopper.Kernel;
using Rhino.Geometry;
using TopoTIN.Core.Engine;
using TopoTIN.Core.Processing;
using TopoTIN.Grasshopper.Utilities;

namespace TopoTIN.Grasshopper.Components;

/// <summary>
/// Primary TIN component: generates a TIN mesh from points and optional breaklines.
/// </summary>
public class TinFromPointsAndBreaklines : GH_Component
{
    private TinEngine _engine = new();

    public TinFromPointsAndBreaklines()
        : base("TIN Surface", "TIN",
               "Generate a TIN surface from points and optional breaklines using constrained Delaunay triangulation.",
               "Mesh", "Triangulation")
    {
    }

    public override Guid ComponentGuid => new("E1A2B3C4-D5E6-7890-ABCD-EF1234567890");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddPointParameter("Points", "P", "Survey points for TIN generation.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Breaklines", "B", "Breakline curves (optional). Mesh edges will follow these exactly.", GH_ParamAccess.list);
        pManager[1].Optional = true;
        pManager.AddNumberParameter("Max Area", "A", "Maximum triangle area for refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("Min Angle", "N", "Minimum triangle angle in degrees for refinement. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[3].Optional = true;
        pManager.AddNumberParameter("Tolerance", "T", "Curve tessellation tolerance. Uses document tolerance if 0.", GH_ParamAccess.item, 0.0);
        pManager[4].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Triangulated mesh.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Face Count", "F", "Number of triangular faces.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Vertex Count", "V", "Number of vertices.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        // Read inputs
        var points = new List<Point3d>();
        if (!DA.GetDataList(0, points) || points.Count < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least 3 points are required.");
            return;
        }

        var curves = new List<Curve>();
        DA.GetDataList(1, curves); // optional

        double maxArea = 0, minAngle = 0, tolerance = 0;
        DA.GetData(2, ref maxArea);
        DA.GetData(3, ref minAngle);
        DA.GetData(4, ref tolerance);

        if (tolerance <= 0)
            tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        // Convert spot points to flat XYZ array
        var spotXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        // Tessellate breakline curves to polylines
        var polylines = new List<double[]>();
        foreach (var crv in curves)
        {
            if (crv == null) continue;

            Polyline pl;
            if (crv.TryGetPolyline(out pl))
            {
                // Already a polyline
            }
            else
            {
                var polyCrv = crv.ToPolyline(tolerance, tolerance, 0.05, 1000);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not tessellate a breakline curve. Skipping.");
                    continue;
                }
            }

            if (pl.Count < 2) continue;
            var flat = new double[pl.Count * 3];
            for (int i = 0; i < pl.Count; i++)
            {
                flat[i * 3] = pl[i].X;
                flat[i * 3 + 1] = pl[i].Y;
                flat[i * 3 + 2] = pl[i].Z;
            }
            polylines.Add(flat);
        }

        // Process breaklines
        var breaklineData = BreaklineDiscretizer.Process(polylines);

        // Merge and deduplicate
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count, breaklineData, tolerance);

        if (merged.DuplicatesRemoved > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{merged.DuplicatesRemoved} duplicate points were merged.");
        if (merged.InvalidsSkipped > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{merged.InvalidsSkipped} invalid points (NaN/Infinity) were skipped.");

        if (merged.VertexCount < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Fewer than 3 unique points after deduplication.");
            return;
        }

        // Build TIN
        var quality = new QualitySettings { MaxArea = maxArea, MinAngle = minAngle };
        var result = _engine.Build(merged.XyCoords, merged.ZValues, merged.Segments, quality);

        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Triangulation failed. Points may be collinear.");
            return;
        }

        // Convert to Rhino mesh
        var mesh = RhinoConverter.ToRhinoMesh(result);

        DA.SetData(0, mesh);
        DA.SetData(1, result.FaceCount);
        DA.SetData(2, result.VertexCount);
    }
}
