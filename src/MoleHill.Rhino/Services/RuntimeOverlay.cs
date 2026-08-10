// Typed, cloneable, non-bakeable viewport primitives and issue metadata for runtime guides/diagnostics.
using System.Drawing;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal enum RuntimeOverlayOwnerKind
{
    Terrain,
    Modifier,
    Analysis,
    Object,
    Tool
}

internal readonly record struct RuntimeOverlayOwner(RuntimeOverlayOwnerKind Kind, Guid Id);

internal enum RuntimeOverlayChannel
{
    Diagnostic,
    Guide
}

internal enum RuntimeOverlaySeverity
{
    Information,
    Warning,
    Error
}

internal enum RuntimeOverlayPrimitiveKind
{
    Marker,
    Dot,
    Text,
    Polyline,
    Mesh
}

/// <summary>
/// Immutable-at-publication viewport geometry. This deliberately exposes a small vocabulary rather
/// than arbitrary GeometryBase instances so diagnostics remain cheap, cloneable, and never bakeable.
/// </summary>
internal sealed class RuntimeOverlayPrimitive
{
    private RuntimeOverlayPrimitive(RuntimeOverlayPrimitiveKind kind)
    {
        Kind = kind;
    }

    public RuntimeOverlayPrimitiveKind Kind { get; }

    public Point3d Point { get; private init; } = Point3d.Unset;

    public Point3d[] Points { get; private init; } = Array.Empty<Point3d>();

    public RhinoMesh? RegionMesh { get; private init; }

    public string? Text { get; private init; }

    public bool IsClosed { get; private init; }

    public int? ColorArgb { get; private init; }

    public int Thickness { get; private init; } = 2;

    public int Size { get; private init; } = 12;

    public bool ShadeMesh { get; private init; } = true;

    public bool DrawMeshWires { get; private init; } = true;

    public double MeshTransparency { get; private init; } = 0.65;

    public static RuntimeOverlayPrimitive Marker(Point3d point, int size = 5, int? colorArgb = null) =>
        new(RuntimeOverlayPrimitiveKind.Marker) { Point = point, Size = Math.Max(1, size), ColorArgb = colorArgb };

    public static RuntimeOverlayPrimitive Dot(Point3d point, string? text = null, int? colorArgb = null) =>
        new(RuntimeOverlayPrimitiveKind.Dot) { Point = point, Text = text, ColorArgb = colorArgb };

    public static RuntimeOverlayPrimitive Label(Point3d point, string text, int size = 12, int? colorArgb = null) =>
        new(RuntimeOverlayPrimitiveKind.Text) { Point = point, Text = text, Size = Math.Max(6, size), ColorArgb = colorArgb };

    public static RuntimeOverlayPrimitive Polyline(
        IEnumerable<Point3d> points,
        bool isClosed = false,
        int thickness = 2,
        int? colorArgb = null) =>
        new(RuntimeOverlayPrimitiveKind.Polyline)
        {
            Points = points.ToArray(),
            IsClosed = isClosed,
            Thickness = Math.Max(1, thickness),
            ColorArgb = colorArgb
        };

    public static RuntimeOverlayPrimitive Mesh(
        RhinoMesh mesh,
        bool shade = true,
        bool drawWires = true,
        double transparency = 0.65,
        int? colorArgb = null) =>
        new(RuntimeOverlayPrimitiveKind.Mesh)
        {
            RegionMesh = mesh.DuplicateMesh(),
            ShadeMesh = shade,
            DrawMeshWires = drawWires,
            MeshTransparency = Math.Clamp(transparency, 0.0, 1.0),
            ColorArgb = colorArgb
        };

    public RuntimeOverlayPrimitive Clone() => new(Kind)
    {
        Point = Point,
        Points = (Point3d[])Points.Clone(),
        RegionMesh = RegionMesh?.DuplicateMesh(),
        Text = Text,
        IsClosed = IsClosed,
        ColorArgb = ColorArgb,
        Thickness = Thickness,
        Size = Size,
        ShadeMesh = ShadeMesh,
        DrawMeshWires = DrawMeshWires,
        MeshTransparency = MeshTransparency
    };

    public BoundingBox GetBounds()
    {
        if (RegionMesh != null)
            return RegionMesh.GetBoundingBox(true);
        if (Points.Length > 0)
            return new BoundingBox(Points);
        return Point.IsValid ? new BoundingBox(Point, Point) : BoundingBox.Empty;
    }

    public int SegmentCost => Kind == RuntimeOverlayPrimitiveKind.Polyline
        ? Math.Max(0, Points.Length - 1) + (IsClosed && Points.Length > 2 ? 1 : 0)
        : 0;

    public int FaceCost => Kind == RuntimeOverlayPrimitiveKind.Mesh ? RegionMesh?.Faces.Count ?? 0 : 0;

    public int AnnotationCost => Kind is RuntimeOverlayPrimitiveKind.Marker or RuntimeOverlayPrimitiveKind.Dot or RuntimeOverlayPrimitiveKind.Text ? 1 : 0;
}

internal sealed class RuntimeOverlayItem
{
    public string StableId { get; init; } = string.Empty;

    public RuntimeOverlayOwner Owner { get; init; }

    public RuntimeOverlayChannel Channel { get; init; } = RuntimeOverlayChannel.Diagnostic;

    public RuntimeOverlaySeverity Severity { get; init; } = RuntimeOverlaySeverity.Information;

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string ShortLabel { get; init; } = string.Empty;

    public int? ColorArgb { get; init; }

    public List<RuntimeOverlayPrimitive> Primitives { get; init; } = new();

    public RuntimeOverlayItem Clone() => new()
    {
        StableId = StableId,
        Owner = Owner,
        Channel = Channel,
        Severity = Severity,
        Code = Code,
        Message = Message,
        ShortLabel = ShortLabel,
        ColorArgb = ColorArgb,
        Primitives = Primitives.Select(primitive => primitive.Clone()).ToList()
    };

    public BoundingBox GetBounds()
    {
        var bounds = BoundingBox.Empty;
        foreach (RuntimeOverlayPrimitive primitive in Primitives)
        {
            BoundingBox primitiveBounds = primitive.GetBounds();
            if (primitiveBounds.IsValid)
                bounds.Union(primitiveBounds);
        }

        return bounds;
    }
}

internal readonly record struct RuntimeDiagnosticSummary(int Errors, int Warnings, int Information)
{
    public int Total => Errors + Warnings + Information;
}

internal static class RuntimeOverlayPalette
{
    public static Color Resolve(RuntimeOverlayItem item, RuntimeOverlayPrimitive primitive)
    {
        int argb = primitive.ColorArgb ?? item.ColorArgb ?? item.Severity switch
        {
            RuntimeOverlaySeverity.Error => Color.FromArgb(224, 58, 52).ToArgb(),
            RuntimeOverlaySeverity.Warning => Color.FromArgb(235, 166, 45).ToArgb(),
            _ => Color.FromArgb(112, 151, 170).ToArgb()
        };
        return Color.FromArgb(argb);
    }
}
