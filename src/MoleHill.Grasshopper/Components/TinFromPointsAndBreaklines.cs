using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Drawing;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Grasshopper.Utilities;
using MoleHill.Shared;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Primary TIN component: generates a TIN mesh from points and/or breaklines.
/// </summary>
public class TinFromPointsAndBreaklines : GH_Component
{
    private TinEngine _engine = new();
    private int _cachedPreprocessHash;
    private bool _hasCachedMerged;
    private PointCloudProcessor.MergedData _cachedMerged;
    private int _cachedConstraintStationCount;
    private int _cachedDirectPolylineCount;
    private int _cachedDirectPolylineVertexCount;

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
        pManager.AddCurveParameter("Boundary", "D", "Optional closed terrain boundary. When omitted, MoleHill may infer a broad footprint from open breakline endpoints.", GH_ParamAccess.list);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("Tolerance", "T", "XY deduplication tolerance. Uses document tolerance if 0.", GH_ParamAccess.item, 0.0);
        pManager[3].Optional = true;
        pManager.AddNumberParameter("Max Edge", "L", "Maximum triangle edge length. 0 = auto-remove outlier boundary triangles. Negative = keep all triangles.", GH_ParamAccess.item, 0.0);
        pManager[4].Optional = true;
        pManager.AddNumberParameter("Max Angle", "A", "Maximum allowed angle for peeled boundary triangles. 170 is the default sliver threshold.", GH_ParamAccess.item, BoundaryTrianglePeelSettings.DefaultMaxInteriorAngleDegrees);
        pManager[5].Optional = true;
        pManager.AddNumberParameter("Slope Limit", "S", "Maximum allowed slope angle for boundary triangles. 0 = ignore slope.", GH_ParamAccess.item, 0.0);
        pManager[6].Optional = true;
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
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(Rhino.RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "MoleHill requires model units. Set Rhino document units to a real length unit, then recompute.");
            return;
        }

        var points = new List<Point3d>();
        DA.GetDataList(0, points);

        var curves = new List<Curve>();
        DA.GetDataList(1, curves);

        var boundaryCurves = new List<Curve>();
        DA.GetDataList(2, boundaryCurves);

        if (points.Count == 0 && curves.Count == 0)
            return;

        double tolerance = 0;
        DA.GetData(3, ref tolerance);

        double maxBoundaryEdgeLength = 0;
        DA.GetData(4, ref maxBoundaryEdgeLength);
        double maxBoundaryAngle = BoundaryTrianglePeelSettings.DefaultMaxInteriorAngleDegrees;
        DA.GetData(5, ref maxBoundaryAngle);
        double maxBoundarySlope = 0.0;
        DA.GetData(6, ref maxBoundarySlope);

        if (tolerance <= 0)
            tolerance = unitContext.AbsoluteTolerance;

        BoundaryTrianglePeelSettings peelSettings = maxBoundaryEdgeLength < 0
            ? BoundaryTrianglePeelSettings.Disabled
            : new BoundaryTrianglePeelSettings
            {
                Enabled = true,
                MaxBoundaryEdgeLength = maxBoundaryEdgeLength,
                MaxInteriorAngleDegrees = maxBoundaryAngle,
                MaxSlopeAngleDegrees = maxBoundarySlope
            };

        int preprocessHash = ComputePreprocessHash(points, curves, tolerance);
        PointCloudProcessor.MergedData merged;

        if (_hasCachedMerged && preprocessHash == _cachedPreprocessHash)
        {
            merged = _cachedMerged;
        }
        else
        {
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
            int directPolylineCount = 0;
            int directPolylineVertexCount = 0;
            foreach (var crv in curves)
            {
                if (crv == null) continue;

                Polyline pl;
                if (crv.TryGetPolyline(out pl))
                {
                    directPolylineCount++;
                    directPolylineVertexCount += pl.Count;
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
            merged = PointCloudProcessor.Merge(spotXyz, points.Count, breaklineData, tolerance);
            _cachedMerged = merged;
            _cachedConstraintStationCount = polylines.Sum(static polyline => polyline.Length / 3);
            _cachedDirectPolylineCount = directPolylineCount;
            _cachedDirectPolylineVertexCount = directPolylineVertexCount;
            _cachedPreprocessHash = preprocessHash;
            _hasCachedMerged = true;
        }

        if (merged.DuplicatesRemoved > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{merged.DuplicatesRemoved} duplicate points merged.");
        if (merged.InvalidsSkipped > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, merged.DescribeInvalidPoints());
        AddRuntimeMessage(
            GH_RuntimeMessageLevel.Remark,
            $"TIN input: {points.Count:N0} spot points, {curves.Count:N0} curves, " +
            $"{_cachedConstraintStationCount:N0} constraint vertices, {merged.VertexCount:N0} unique vertices; " +
            $"tolerance {tolerance:G6}; direct polylines {_cachedDirectPolylineCount:N0}/{curves.Count:N0} " +
            $"({_cachedDirectPolylineVertexCount:N0} source vertices).");

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

        if (TryBuildValidatedTinMesh(merged.XyCoords, merged.ZValues, merged.Segments, boundaryCurves, tolerance, quality, peelSettings,
            out var result, out var mesh, out string? buildMessage))
        {
            if (!string.IsNullOrWhiteSpace(buildMessage))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, buildMessage);

            DA.SetData(0, mesh!);
            DA.SetDataList(1, RhinoConverter.ToEdgeLines(result!));
            DA.SetDataList(2, RhinoConverter.ToNakedEdgeLines(result!));
            DA.SetDataList(3, RhinoConverter.ToPoints(result!));
            DA.SetData(4, result!.FaceCount);
            return;
        }

        var cleanup = TinInputCleaner.Clean(merged, tolerance);
        if (!cleanup.HasChanges)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, buildMessage ?? "Triangulation failed.");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Automatic input cleanup made no safe changes: {cleanup.ToDiagnosticSummary()}.");
            return;
        }

        if (cleanup.VertexCount < 3)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, buildMessage ?? "Triangulation failed.");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Automatic input cleanup reduced the dataset below three usable vertices: {cleanup.ToDiagnosticSummary()}.");
            return;
        }

        if (!TryBuildValidatedTinMesh(cleanup.XyCoords, cleanup.ZValues, cleanup.Segments, boundaryCurves, tolerance, quality, peelSettings,
            out result, out mesh, out string? cleanupMessage))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, buildMessage ?? "Triangulation failed.");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Automatic input cleanup retry failed: {cleanup.ToDiagnosticSummary()}.");
            if (!string.IsNullOrWhiteSpace(cleanupMessage))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, cleanupMessage);
            return;
        }

        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Automatic input cleanup retry succeeded: {cleanup.ToDiagnosticSummary()}.");
        if (!string.IsNullOrWhiteSpace(cleanupMessage))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, cleanupMessage);

        DA.SetData(0, mesh!);
        DA.SetDataList(1, RhinoConverter.ToEdgeLines(result!));
        DA.SetDataList(2, RhinoConverter.ToNakedEdgeLines(result!));
        DA.SetDataList(3, RhinoConverter.ToPoints(result!));
        DA.SetData(4, result!.FaceCount);
    }

    private bool TryBuildValidatedTinMesh(
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        IReadOnlyList<Curve> boundaryCurves,
        double tolerance,
        QualitySettings quality,
        BoundaryTrianglePeelSettings peelSettings,
        out TinResult? result,
        out Mesh? mesh,
        out string? message)
    {
        var prepared = TinBoundaryPreparer.Prepare(
            xyCoords,
            zValues,
            segments,
            CreateBoundaryPolylines(boundaryCurves, tolerance),
            tolerance);

        mesh = null;

        result = _engine.Build(
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            quality,
            out message,
            useConvexHull: prepared.UseConvexHull,
            boundaryPeelSettings: peelSettings);

        if (!string.IsNullOrWhiteSpace(prepared.WarningMessage))
            message = AppendMessage(message, prepared.WarningMessage);
        if (!string.IsNullOrWhiteSpace(prepared.InfoMessage))
            message = AppendMessage(message, prepared.InfoMessage);

        if (result == null)
            return false;

        try
        {
            mesh = RhinoGeometryConversions.ToRhinoMesh(result);
        }
        catch (Exception ex)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? $"Triangulation produced an invalid mesh: {ex.Message}"
                : $"{message} Triangulation produced an invalid mesh: {ex.Message}";
            result = null;
            mesh = null;
            return false;
        }

        if (mesh.Faces.Count == 0 || mesh.Vertices.Count == 0 || !mesh.IsValid)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? "Triangulation produced an invalid mesh."
                : $"{message} Triangulation produced an invalid mesh.";
            result = null;
            mesh = null;
            return false;
        }

        return true;
    }

    private static TinBoundaryPreparer.BoundaryPolyline[] CreateBoundaryPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        var result = new List<TinBoundaryPreparer.BoundaryPolyline>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            Polyline polyline;
            if (curve.TryGetPolyline(out polyline))
            {
            }
            else
            {
                var polyCurve = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCurve == null || !polyCurve.TryGetPolyline(out polyline))
                    continue;
            }

            if (polyline.Count < 2)
                continue;

            var flat = new double[polyline.Count * 3];
            for (int i = 0; i < polyline.Count; i++)
            {
                flat[i * 3] = polyline[i].X;
                flat[i * 3 + 1] = polyline[i].Y;
                flat[i * 3 + 2] = polyline[i].Z;
            }

            result.Add(new TinBoundaryPreparer.BoundaryPolyline(flat, polyline.Count, curve.IsClosed));
        }

        return result.ToArray();
    }

    private static string AppendMessage(string? current, string next)
    {
        return string.IsNullOrWhiteSpace(current)
            ? next
            : $"{current} {next}";
    }

    private static int ComputePreprocessHash(IReadOnlyList<Point3d> points, IReadOnlyList<Curve> curves, double tolerance)
    {
        var hasher = new HashCode();

        hasher.Add(points.Count);
        if (points.Count > 0)
        {
            var pointKeys = new (long x, long y, long z)[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                pointKeys[i] = (
                    BitConverter.DoubleToInt64Bits(p.X),
                    BitConverter.DoubleToInt64Bits(p.Y),
                    BitConverter.DoubleToInt64Bits(p.Z));
            }

            Array.Sort(pointKeys, static (a, b) =>
            {
                int cx = a.x.CompareTo(b.x);
                if (cx != 0) return cx;
                int cy = a.y.CompareTo(b.y);
                if (cy != 0) return cy;
                return a.z.CompareTo(b.z);
            });

            foreach (var key in pointKeys)
            {
                hasher.Add(key.x);
                hasher.Add(key.y);
                hasher.Add(key.z);
            }
        }

        hasher.Add(curves.Count);
        if (curves.Count > 0)
        {
            var curveKeys = new int[curves.Count];
            for (int i = 0; i < curves.Count; i++)
            {
                var curve = curves[i];
                curveKeys[i] = curve == null
                    ? int.MinValue
                    : unchecked((int)curve.DataCRC(0u));
            }

            Array.Sort(curveKeys);
            foreach (int key in curveKeys)
                hasher.Add(key);
        }

        hasher.Add(BitConverter.DoubleToInt64Bits(tolerance));
        return hasher.ToHashCode();
    }
}
