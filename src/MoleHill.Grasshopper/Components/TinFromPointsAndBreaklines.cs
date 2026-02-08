using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Drawing;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Grasshopper.Utilities;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Primary TIN component: generates a TIN mesh from points and/or breaklines.
/// </summary>
public class TinFromPointsAndBreaklines : GH_Component
{
    private TinEngine _engine = new();

    public TinFromPointsAndBreaklines()
        : base("TIN Surface", "TIN",
               "Generate a TIN surface from points and/or breaklines using constrained Delaunay triangulation.",
               "MoleHill", "Surface")
    {
    }

    protected override Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.TinSurface.png");

    public override Guid ComponentGuid => new("E1A2B3C4-D5E6-7890-ABCD-EF1234567890");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddPointParameter("Points", "P", "Survey points for TIN generation (optional if breaklines provided).", GH_ParamAccess.list);
        pManager[0].Optional = true;
        pManager.AddCurveParameter("Breaklines", "B", "Breakline/contour curves (optional). Mesh edges will follow these exactly.", GH_ParamAccess.list);
        pManager[1].Optional = true;
        pManager.AddNumberParameter("Tolerance", "T", "XY deduplication tolerance. Uses document tolerance if 0.", GH_ParamAccess.item, 0.0);
        pManager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Triangulated mesh.", GH_ParamAccess.item);
        pManager.AddLineParameter("Edges", "E", "All mesh edges.", GH_ParamAccess.list);
        pManager.AddLineParameter("Naked Edges", "NE", "Boundary (naked) edges.", GH_ParamAccess.list);
        pManager.AddPointParameter("Vertices", "V", "Mesh vertices.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Face Count", "F", "Number of triangular faces.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var points = new List<Point3d>();
        DA.GetDataList(0, points);

        var curves = new List<Curve>();
        DA.GetDataList(1, curves);

        if (points.Count == 0 && curves.Count == 0)
            return;

        double tolerance = 0;
        DA.GetData(2, ref tolerance);

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
                var polyCrv = crv.ToPolyline(
                    tolerance,           // distance tolerance
                    Math.PI / 36.0,      // 5 degree angle tolerance (radians)
                    0.0,                 // minimum edge length
                    0.0                  // maximum edge length (0 = no limit)
                );
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
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{merged.DuplicatesRemoved} duplicate points merged.");
        if (merged.InvalidsSkipped > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{merged.InvalidsSkipped} invalid points (NaN/Infinity) skipped.");

        if (merged.VertexCount < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                $"Only {merged.VertexCount} unique point(s) after deduplication (need 3+). "
                + $"Input: {points.Count} points, {curves.Count} curves. "
                + $"Try reducing the tolerance (currently {tolerance}).");
            return;
        }

        // Build TIN (pure CDT, no quality refinement — use Remesh for that)
        var quality = QualitySettings.None;
        _engine.InvalidateCache();

        var result = _engine.Build(merged.XyCoords, merged.ZValues, merged.Segments, quality,
                                   out string? errorMessage);

        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, errorMessage ?? "Triangulation failed.");
            return;
        }

        // If there was a warning (e.g. fallback to plain Delaunay)
        if (errorMessage != null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, errorMessage);

        DA.SetData(0, RhinoConverter.ToRhinoMesh(result));
        DA.SetDataList(1, RhinoConverter.ToEdgeLines(result));
        DA.SetDataList(2, RhinoConverter.ToNakedEdgeLines(result));
        DA.SetDataList(3, RhinoConverter.ToPoints(result));
        DA.SetData(4, result.FaceCount);
    }
}
