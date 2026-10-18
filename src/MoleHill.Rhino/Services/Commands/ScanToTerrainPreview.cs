using System.Drawing;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Disposable, document-scoped viewport output. All native geometry is created on the UI
/// thread; preview never adds objects, changes source visibility or creates an Undo record.</summary>
internal sealed class ScanToTerrainPreview(uint documentSerial) : DisplayConduit, IDisposable
{
    private readonly List<(Curve Curve, Color Colour)> _curves = new();
    private readonly List<(Mesh Mesh, DisplayMaterial Material)> _meshes = new();
    private readonly List<(Point3d[] Points, Color Colour)> _points = new();

    public void Update(ScanTerrainPipeline.Result? pipeline, TerrainCurveRecovery.Result? recovery, ISet<string> show)
    {
        Clear();
        if (pipeline == null) return;
        if (show.Contains(nameof(ScanToTerrainSettings.AddGroundMesh)))
            _meshes.Add((MeshOf(pipeline.Reference), new DisplayMaterial(Color.LightGray) { Transparency = .55 }));
        if (show.Contains(nameof(ScanToTerrainSettings.Buildings)) && pipeline.Buildings != null)
            foreach (ScanBuildings.Building building in pipeline.Buildings.Buildings)
                _meshes.Add((MeshOf(building.Solid), new DisplayMaterial(building.IsFallback ? Color.IndianRed : Color.BurlyWood) { Transparency = .2 }));
        if (show.Contains(nameof(ScanToTerrainSettings.Walls)))
            foreach (ScanWalls.Wall wall in pipeline.Walls)
            {
                _curves.Add((RouteCurveBuilder.FromFitted(wall.TopCurve), Color.SaddleBrown));
                _curves.Add((RouteCurveBuilder.FromFitted(wall.ToeCurve), Color.SaddleBrown));
            }
        if (show.Contains(nameof(ScanToTerrainSettings.Surfaces)) && pipeline.Areas != null)
        {
            foreach (ScanSurfaceAreas.Boundary area in pipeline.Areas.Boundaries)
                _curves.Add((RouteCurveBuilder.FromFitted(area.Curve), area.Kind == ScanSurfaceKind.Paved ? Color.DimGray : Color.ForestGreen));
            var paved = new List<Point3d>(); var soft = new List<Point3d>();
            ScanGrid grid = pipeline.Ground.Ground;
            int stride = Math.Max(1, (int)Math.Ceiling(grid.NodeCount / 50000.0));
            for (int node = 0; node < grid.NodeCount; node += stride)
            {
                ScanSurfaceKind kind = pipeline.Areas.PavedMask[node] ? ScanSurfaceKind.Paved :
                    pipeline.Areas.SoftMask[node] ? ScanSurfaceKind.Soft : ScanSurfaceKind.Unknown;
                if (kind is not (ScanSurfaceKind.Paved or ScanSurfaceKind.Soft) || !double.IsFinite(grid.Values[node])) continue;
                var point = new Point3d(grid.NodeX(node % grid.Columns), grid.NodeY(node / grid.Columns), grid.Values[node]);
                (kind == ScanSurfaceKind.Paved ? paved : soft).Add(point);
            }
            _points.Add((paved.ToArray(), Color.Gray)); _points.Add((soft.ToArray(), Color.LimeGreen));
        }
        if (show.Contains(nameof(ScanToTerrainSettings.RoadEdges)))
            foreach (ScanRoadEdges.EdgeLine edge in pipeline.Roads)
            {
                _curves.Add((RouteCurveBuilder.FromFitted(edge.BottomCurve), Color.DarkOrange));
                if (edge.TopCurve != null) _curves.Add((RouteCurveBuilder.FromFitted(edge.TopCurve), Color.DarkOrange));
            }
        if (recovery == null) return;
        foreach (double[] border in recovery.Borders) _curves.Add((Polyline(border, true), Color.Black));
        if (show.Contains(nameof(ScanToTerrainSettings.Contours)))
        {
            foreach (var contour in recovery.Contours)
                _curves.Add((contour.Curve == null ? Polyline(contour.PointsXyz, contour.IsClosed) : RouteCurveBuilder.FromFitted(contour.Curve), Color.DarkGoldenrod));
            _points.Add((Points(recovery.SpotPoints), Color.SeaGreen));
        }
        if (show.Contains(nameof(ScanToTerrainSettings.RidgesValleys)))
            foreach (double[] line in recovery.LandformLines) _curves.Add((Polyline(line, false), Color.Firebrick));
    }

    private static Point3d[] Points(double[] xyz)
    {
        var points = new Point3d[xyz.Length / 3];
        for (int i = 0; i < points.Length; i++) points[i] = new Point3d(xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]);
        return points;
    }
    private static Curve Polyline(double[] xyz, bool closed)
    {
        var points = Points(xyz).ToList(); if (closed && points.Count > 0) points.Add(points[0]);
        return new PolylineCurve(points);
    }
    private static Mesh MeshOf(IndexedTriMesh source)
    {
        var mesh = new Mesh(); mesh.Vertices.UseDoublePrecisionVertices = true;
        for (int i = 0; i < source.VertexCount; i++) mesh.Vertices.Add(source.Vertices[i * 3], source.Vertices[i * 3 + 1], source.Vertices[i * 3 + 2]);
        for (int i = 0; i < source.FaceCount; i++) mesh.Faces.AddFace(source.Faces[i * 3], source.Faces[i * 3 + 1], source.Faces[i * 3 + 2]);
        mesh.Normals.ComputeNormals(); return mesh;
    }
    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
        foreach (var item in _meshes) e.IncludeBoundingBox(item.Mesh.GetBoundingBox(false));
        foreach (var item in _curves) e.IncludeBoundingBox(item.Curve.GetBoundingBox(false));
        foreach (var item in _points) if (item.Points.Length > 0) e.IncludeBoundingBox(new BoundingBox(item.Points));
    }
    protected override void DrawForeground(DrawEventArgs e)
    {
        if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
        e.Display.PushDepthTesting(false); e.Display.PushDepthWriting(false);
        try
        {
            foreach (var item in _meshes) e.Display.DrawMeshShaded(item.Mesh, item.Material);
            foreach (var item in _curves) e.Display.DrawCurve(item.Curve, item.Colour, 2);
            foreach (var item in _points) e.Display.DrawPoints(item.Points, PointStyle.Simple, 2, item.Colour);
        }
        finally { e.Display.PopDepthWriting(); e.Display.PopDepthTesting(); }
    }
    private void Clear()
    {
        foreach (var item in _curves) item.Curve.Dispose(); _curves.Clear();
        foreach (var item in _meshes) { item.Mesh.Dispose(); item.Material.Dispose(); } _meshes.Clear(); _points.Clear();
    }
    public void Dispose() { Enabled = false; Clear(); }
}
