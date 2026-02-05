using Grasshopper.Kernel;
using Rhino.Geometry;
using TopoTIN.Core.Engine;
using TopoTIN.Core.Processing;
using TopoTIN.Grasshopper.Utilities;

namespace TopoTIN.Grasshopper.Components;

/// <summary>
/// Deconstruct a TIN mesh into its constituent parts:
/// vertices, faces, edges, and naked (boundary) edges.
/// </summary>
public class TinDeconstructComponent : GH_Component
{
    private TinEngine _engine = new();

    public TinDeconstructComponent()
        : base("Deconstruct TIN", "dTIN",
               "Deconstruct a TIN surface into vertices, face indices, edges, and naked edges.",
               "Mesh", "Triangulation")
    {
    }

    public override Guid ComponentGuid => new("E3A2B3C4-D5E6-7890-ABCD-EF1234567890");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddPointParameter("Points", "P", "Survey points.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Breaklines", "B", "Breakline curves (optional).", GH_ParamAccess.list);
        pManager[1].Optional = true;
        pManager.AddNumberParameter("Tolerance", "T", "Curve tessellation tolerance. Uses document tolerance if 0.", GH_ParamAccess.item, 0.0);
        pManager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddPointParameter("Vertices", "V", "Mesh vertices.", GH_ParamAccess.list);
        pManager.AddMeshParameter("Mesh", "M", "Triangulated mesh.", GH_ParamAccess.item);
        pManager.AddLineParameter("Edges", "E", "All mesh edges.", GH_ParamAccess.list);
        pManager.AddLineParameter("Naked Edges", "NE", "Boundary (naked) edges.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var points = new List<Point3d>();
        if (!DA.GetDataList(0, points) || points.Count < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least 3 points are required.");
            return;
        }

        var curves = new List<Curve>();
        DA.GetDataList(1, curves);

        double tolerance = 0;
        DA.GetData(2, ref tolerance);
        if (tolerance <= 0)
            tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        // Convert spot points
        var spotXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        // Tessellate breaklines
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
                    continue;
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

        var breaklineData = BreaklineDiscretizer.Process(polylines);
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count, breaklineData, tolerance);

        if (merged.VertexCount < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Fewer than 3 unique points after deduplication.");
            return;
        }

        var quality = QualitySettings.None;
        var result = _engine.Build(merged.XyCoords, merged.ZValues, merged.Segments, quality);

        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Triangulation failed.");
            return;
        }

        DA.SetDataList(0, RhinoConverter.ToPoints(result));
        DA.SetData(1, RhinoConverter.ToRhinoMesh(result));
        DA.SetDataList(2, RhinoConverter.ToEdgeLines(result));
        DA.SetDataList(3, RhinoConverter.ToNakedEdgeLines(result));
    }
}
