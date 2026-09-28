using Eto.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Renders a small isometric, shaded thumbnail of a block definition's geometry for the block selector.
/// Self-contained software projection (no Rhino render pipeline): the block's render meshes are painter-
/// sorted and filled with simple directional shading; curves are drawn as wireframe. Thumbnails are
/// cached per document, definition id and requested size. Definition-table changes invalidate that
/// document's entries so renamed or redefined blocks cannot reuse stale imagery. Returns null when the
/// block has no drawable geometry.
/// </summary>
internal static class BlockThumbnailRenderer
{
    private readonly record struct CacheKey(uint DocumentSerial, Guid DefinitionId, int Size);

    private sealed class CacheEntry
    {
        public CacheEntry(Bitmap? bitmap)
        {
            Bitmap = bitmap;
        }

        public Bitmap? Bitmap { get; }
    }

    private static readonly object CacheGate = new();
    private static readonly Dictionary<CacheKey, CacheEntry> Cache = new();
    private const int MaxTriangles = 20000;

    static BlockThumbnailRenderer()
    {
        RhinoDoc.InstanceDefinitionTableEvent += OnInstanceDefinitionTableEvent;
        RhinoDoc.CloseDocument += OnCloseDocument;
    }

    public static Bitmap? Get(RhinoDoc doc, InstanceDefinition definition, int size)
    {
        if (doc == null || definition == null || definition.Id == Guid.Empty || size <= 0)
            return null;

        var key = new CacheKey(doc.RuntimeSerialNumber, definition.Id, size);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out CacheEntry? cached))
                return cached.Bitmap;
        }

        Bitmap? rendered = Render(definition, size);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out CacheEntry? cached))
            {
                rendered?.Dispose();
                return cached.Bitmap;
            }

            Cache[key] = new CacheEntry(rendered);
            return rendered;
        }
    }

    private static void OnInstanceDefinitionTableEvent(object? sender, InstanceDefinitionTableEventArgs e) =>
        InvalidateDocument(e.Document.RuntimeSerialNumber);

    private static void OnCloseDocument(object? sender, DocumentEventArgs e) =>
        InvalidateDocument(e.Document.RuntimeSerialNumber);

    private static void InvalidateDocument(uint documentSerial)
    {
        lock (CacheGate)
        {
            CacheKey[] keys = Cache.Keys
                .Where(key => key.DocumentSerial == documentSerial)
                .ToArray();
            foreach (CacheKey key in keys)
            {
                if (Cache.Remove(key, out CacheEntry? entry))
                    entry.Bitmap?.Dispose();
            }
        }
    }

    private static Bitmap? Render(InstanceDefinition definition, int size)
    {
        try
        {
            var triangles = new List<(Point3d A, Point3d B, Point3d C)>();
            var curves = new List<Point3d[]>();
            CollectGeometry(definition, triangles, curves);
            if (triangles.Count == 0 && curves.Count == 0)
                return null;

            BoundingBox bounds = BoundingBox.Empty;
            foreach (var (a, b, c) in triangles)
            {
                bounds.Union(a);
                bounds.Union(b);
                bounds.Union(c);
            }
            foreach (Point3d[] curve in curves)
                foreach (Point3d p in curve)
                    bounds.Union(p);

            if (!bounds.IsValid)
                return null;

            // Project to 2:1 isometric, then fit to the bitmap with padding.
            static PointF Project(Point3d p) => new((float)(p.X - p.Y), (float)((p.X + p.Y) * 0.5 - p.Z));

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            void Track(PointF p)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            foreach (var (a, b, c) in triangles)
            {
                Track(Project(a));
                Track(Project(b));
                Track(Project(c));
            }
            foreach (Point3d[] curve in curves)
                foreach (Point3d p in curve)
                    Track(Project(p));

            float spanX = Math.Max(maxX - minX, 1e-6f);
            float spanY = Math.Max(maxY - minY, 1e-6f);
            const float padding = 4f;
            float scale = Math.Min((size - 2 * padding) / spanX, (size - 2 * padding) / spanY);
            float offsetX = padding + (size - 2 * padding - spanX * scale) * 0.5f;
            float offsetY = padding + (size - 2 * padding - spanY * scale) * 0.5f;

            PointF ToScreen(Point3d p)
            {
                PointF q = Project(p);
                // Flip Y so up is up on screen.
                return new PointF(offsetX + (q.X - minX) * scale, size - (offsetY + (q.Y - minY) * scale));
            }

            var light = new Vector3d(-0.4, -0.5, 0.9);
            light.Unitize();
            var baseColor = Color.FromArgb(150, 170, 190);

            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppRgba);
            using (var g = new Graphics(bitmap))
            {
                g.Clear(Colors.Transparent);
                g.AntiAlias = true;

                // Painter's algorithm: draw far faces first (smaller x+y+z is farther from the +X+Y+Z eye).
                triangles.Sort((t1, t2) => Depth(t1).CompareTo(Depth(t2)));
                var edgePen = new Pen(Color.FromArgb(60, 70, 80), 1f);
                foreach (var (a, b, c) in triangles)
                {
                    var normal = Vector3d.CrossProduct(b - a, c - a);
                    if (!normal.Unitize())
                        continue;

                    double brightness = Math.Clamp(0.35 + 0.65 * Math.Max(0.0, normal * light), 0.0, 1.0);
                    var faceColor = Color.FromArgb(
                        (int)(baseColor.R * 255 * brightness),
                        (int)(baseColor.G * 255 * brightness),
                        (int)(baseColor.B * 255 * brightness));
                    PointF[] poly = { ToScreen(a), ToScreen(b), ToScreen(c) };
                    g.FillPolygon(faceColor, poly);
                    g.DrawPolygon(edgePen, poly);
                }

                var curvePen = new Pen(Color.FromArgb(40, 50, 60), 1.4f);
                foreach (Point3d[] curve in curves)
                {
                    if (curve.Length < 2)
                        continue;

                    var screen = new PointF[curve.Length];
                    for (int i = 0; i < curve.Length; i++)
                        screen[i] = ToScreen(curve[i]);
                    g.DrawLines(curvePen, screen);
                }
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static double Depth((Point3d A, Point3d B, Point3d C) tri) =>
        (tri.A.X + tri.A.Y + tri.A.Z + tri.B.X + tri.B.Y + tri.B.Z + tri.C.X + tri.C.Y + tri.C.Z) / 3.0;

    private static void CollectGeometry(
        InstanceDefinition definition,
        List<(Point3d A, Point3d B, Point3d C)> triangles,
        List<Point3d[]> curves)
    {
        foreach (RhinoObject obj in definition.GetObjects())
        {
            if (triangles.Count >= MaxTriangles)
                return;

            GeometryBase? geometry = obj?.Geometry;
            switch (geometry)
            {
                case Mesh mesh:
                    AddMesh(mesh, triangles);
                    break;
                case Brep brep:
                    foreach (Mesh m in Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>())
                        AddMesh(m, triangles);
                    break;
                case Extrusion extrusion when extrusion.ToBrep() is { } extrusionBrep:
                    foreach (Mesh m in Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>())
                        AddMesh(m, triangles);
                    break;
                case Curve curve:
                    AddCurve(curve, curves);
                    break;
            }
        }
    }

    private static void AddMesh(Mesh mesh, List<(Point3d A, Point3d B, Point3d C)> triangles)
    {
        var vertices = mesh.Vertices.ToPoint3dArray();
        foreach (MeshFace face in mesh.Faces)
        {
            if (triangles.Count >= MaxTriangles)
                return;

            triangles.Add((vertices[face.A], vertices[face.B], vertices[face.C]));
            if (face.IsQuad)
                triangles.Add((vertices[face.A], vertices[face.C], vertices[face.D]));
        }
    }

    private static void AddCurve(Curve curve, List<Point3d[]> curves)
    {
        if (curve.TryGetPolyline(out Polyline polyline) && polyline.Count >= 2)
        {
            curves.Add(polyline.ToArray());
            return;
        }

        double[]? parameters = curve.DivideByCount(32, includeEnds: true);
        if (parameters == null || parameters.Length < 2)
            return;

        curves.Add(parameters.Select(curve.PointAt).ToArray());
    }
}
