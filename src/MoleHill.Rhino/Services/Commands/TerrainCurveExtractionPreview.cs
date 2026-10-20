using System.Drawing;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Disposable, document-scoped viewport output. All native geometry is created on the UI
/// thread; preview never adds objects, changes source visibility or creates an Undo record.</summary>
internal sealed class TerrainCurveExtractionPreview(uint documentSerial, Guid? sourceId = null) : DisplayConduit, IDisposable
{
    public bool ShowSource { get; set; }
    private readonly List<(Curve Curve, Color Colour, int Weight)> _curves = new();
    private readonly List<(Mesh Mesh, DisplayMaterial Material)> _meshes = new();
    private readonly List<(Mesh Mesh, DisplayMaterial Material)> _buildings = new();
    private readonly List<(Point3d[] Points, Color Colour, int Size)> _points = new();
    // Building creases, drawn over the source instead of the solid: a solid on the scanned roof z-fights with it.
    private readonly List<(Line[] Lines, Color Colour)> _creases = new();

    public void Update(TerrainCurveExtractionPrepared? prepared, ISet<string> show)
    {
        Clear();
        if (prepared == null) return;
        if (show.Contains("Reference"))
            _meshes.Add((MeshOf(prepared.Reference), new DisplayMaterial(Color.LightGray) { Transparency = .55 }));
        var pipeline = prepared.Scan;
        if (pipeline != null)
        {
            if (show.Contains(nameof(ScanToTerrainSettings.AddGroundMesh)))
                _meshes.Add((MeshOf(pipeline.Reference), new DisplayMaterial(TerrainCurveExtractionStyle.Ground.Colour) { Transparency = .55 }));
            if (show.Contains(nameof(ScanToTerrainSettings.Buildings)) && pipeline.Buildings != null)
                foreach (ScanBuildings.Building building in pipeline.Buildings.Buildings)
                {
                    Color colour = building.IsFallback ? Color.IndianRed : TerrainCurveExtractionStyle.Buildings.Colour;
                    Mesh solid = MeshOf(building.Solid);
                    _creases.Add((Creases(solid), Color.FromArgb(colour.R * 3 / 4, colour.G * 3 / 4, colour.B * 3 / 4)));
                    _buildings.Add((solid, new DisplayMaterial(colour) { Transparency = .2 }));
                }
            if (show.Contains(nameof(ScanToTerrainSettings.Walls)))
                foreach (ScanWalls.Wall wall in pipeline.Walls)
                {
                    Add(ScanCurveBuilder.Build(wall.TopCurve), TerrainCurveExtractionStyle.Walls);
                    Add(ScanCurveBuilder.Build(wall.ToeCurve), TerrainCurveExtractionStyle.Walls);
                }
            if (show.Contains(nameof(ScanToTerrainSettings.Surfaces)) && pipeline.Areas != null && pipeline.IncludeAreaBoundaries)
            {
                foreach (ScanSurfaceAreas.Boundary area in pipeline.Areas.Boundaries.Where(b => !b.IsSharedDuplicate))
                    Add(ScanCurveBuilder.Build(area.Curve), area.Kind == ScanSurfaceKind.Paved ? TerrainCurveExtractionStyle.PavedAreas : TerrainCurveExtractionStyle.SoftAreas);
                if (show.Contains("SurfaceClasses"))
                {
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
                    _points.Add((paved.ToArray(), TerrainCurveExtractionStyle.PavedAreas.Colour, 2)); _points.Add((soft.ToArray(), TerrainCurveExtractionStyle.SoftAreas.Colour, 2));
                }
            }
            if (show.Contains(nameof(ScanToTerrainSettings.RoadEdges)))
                foreach (ScanRoadEdges.EdgeLine edge in pipeline.Roads)
                {
                    if (!edge.IsKerb && pipeline.IncludeAreaBoundaries && pipeline.Areas != null) continue;
                    var style = edge.IsKerb ? TerrainCurveExtractionStyle.Kerbs : TerrainCurveExtractionStyle.PavingEdges;
                    Add(ScanCurveBuilder.Build(edge.BottomCurve), style);
                    if (edge.TopCurve != null) Add(ScanCurveBuilder.Build(edge.TopCurve), style);
                }
        }
        var recovery = prepared.Recovery;
        if (recovery == null) return;
        foreach (double[] border in recovery.Borders) Add(Polyline(border, true), TerrainCurveExtractionStyle.Border);
        if (show.Contains(nameof(TerrainCurveExtractionSettings.Contours)))
        {
            foreach (var contour in recovery.Contours)
                Add(TerrainCurveExtractionGeometry.Contour(contour), TerrainCurveExtractionStyle.Contours);
            _points.Add((Points(recovery.SpotPoints), TerrainCurveExtractionStyle.Spots.Colour, TerrainCurveExtractionStyle.Spots.Weight));
        }
        if (show.Contains(nameof(TerrainCurveExtractionSettings.RidgesValleys)))
            foreach (double[] line in recovery.LandformLines) Add(Polyline(line, false), TerrainCurveExtractionStyle.RidgesValleys);
    }

    private void Add(Curve curve, TerrainCurveExtractionStyle.Style style) => _curves.Add((curve, style.Colour, style.Weight));

    protected override void PreDrawObject(DrawObjectEventArgs e)
    {
        if (!ShowSource && sourceId.HasValue && e.RhinoDoc.RuntimeSerialNumber == documentSerial && e.RhinoObject?.Id == sourceId.Value)
            e.DrawObject = false;
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
    /// <summary>Edges where faces meet at more than 20 degrees: eaves, ridges and wall corners.</summary>
    private static Line[] Creases(Mesh mesh)
    {
        mesh.FaceNormals.ComputeFaceNormals();
        var lines = new List<Line>();
        var topology = mesh.TopologyEdges;
        for (int edge = 0; edge < topology.Count; edge++)
        {
            int[] faces = topology.GetConnectedFaces(edge);
            if (faces.Length == 2 && Vector3d.VectorAngle(mesh.FaceNormals[faces[0]], mesh.FaceNormals[faces[1]]) < RhinoMath.ToRadians(20)) continue;
            lines.Add(topology.EdgeLine(edge));
        }
        return lines.ToArray();
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
        foreach (var item in _meshes.Concat(_buildings)) e.IncludeBoundingBox(item.Mesh.GetBoundingBox(false));
        foreach (var item in _curves) e.IncludeBoundingBox(item.Curve.GetBoundingBox(false));
        foreach (var item in _points) if (item.Points.Length > 0) e.IncludeBoundingBox(new BoundingBox(item.Points));
    }
    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
        foreach (var item in _meshes) e.Display.DrawMeshShaded(item.Mesh, item.Material);
        if (!ShowSource)
            foreach (var item in _buildings) e.Display.DrawMeshShaded(item.Mesh, item.Material);
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
        e.Display.PushDepthTesting(false); e.Display.PushDepthWriting(false);
        try
        {
            if (ShowSource)
                foreach (var item in _creases) e.Display.DrawLines(item.Lines, item.Colour, 2);
            foreach (var item in _curves) e.Display.DrawCurve(item.Curve, item.Colour, item.Weight);
            foreach (var item in _points) e.Display.DrawPoints(item.Points, PointStyle.RoundSimple, item.Size, item.Colour);
        }
        finally { e.Display.PopDepthWriting(); e.Display.PopDepthTesting(); }
    }
    private void Clear()
    {
        foreach (var item in _curves) item.Curve.Dispose(); _curves.Clear();
        foreach (var item in _meshes.Concat(_buildings)) { item.Mesh.Dispose(); item.Material.Dispose(); }
        _meshes.Clear(); _buildings.Clear(); _points.Clear(); _creases.Clear();
    }
    public void Dispose() { Enabled = false; Clear(); }
}
