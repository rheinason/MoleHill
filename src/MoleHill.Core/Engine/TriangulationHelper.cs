using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Engine;

/// <summary>
/// Shared triangulation with multi-tier fallback.
/// Used by TinEngine, PadGrader, RemeshComponent, MeshAreaSplitter.
/// </summary>
public static class TriangulationHelper
{
    /// <summary>
    /// Triangulate vertices + segments with automatic fallback:
    /// 1. Conforming CDT + quality
    /// 2. Non-conforming CDT + quality
    /// 3. Conforming CDT, no quality
    /// 4. Non-conforming CDT, no quality
    /// 5. Plain Delaunay (drops segments)
    /// </summary>
    public static IMesh? Triangulate(
        List<double> xyList, int vertexCount,
        List<(int a, int b)> segments,
        double maxArea, double minAngle,
        out string? warning,
        bool convex = true)
    {
        warning = null;
        bool hasSegs = segments.Count > 0;
        bool hasQuality = maxArea > 0 || minAngle > 0;
        var mesher = new GenericMesher();

        Polygon BuildPolygon(bool includeSegs)
        {
            var polygon = new Polygon(vertexCount);
            var verts = new Vertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var v = new Vertex(xyList[i * 2], xyList[i * 2 + 1]) { ID = i };
                verts[i] = v;
                polygon.Add(v);
            }
            if (includeSegs)
            {
                foreach (var (a, b) in segments)
                {
                    if (a >= 0 && a < vertexCount && b >= 0 && b < vertexCount)
                        polygon.Add(new Segment(verts[a], verts[b], 1), false);
                }
            }
            return polygon;
        }

        var constrainedPolygon = BuildPolygon(includeSegs: true);
        var unconstrainedPolygon = hasSegs ? BuildPolygon(includeSegs: false) : constrainedPolygon;

        QualityOptions? quality = null;
        if (hasQuality)
        {
            quality = new QualityOptions();
            if (maxArea > 0) quality.MaximumArea = maxArea;
            if (minAngle > 0) quality.MinimumAngle = minAngle;
        }

        IMesh? TryMesh(Polygon poly, bool conforming, QualityOptions? qualityOptions)
        {
            var opts = new ConstraintOptions
            {
                ConformingDelaunay = conforming && hasSegs,
                Convex = convex
            };
            try
            {
                var mesh = mesher.Triangulate(poly, opts, qualityOptions);
                return mesh.Triangles.Count > 0 ? mesh : null;
            }
            catch { return null; }
        }

        // Tier 1: Conforming CDT + quality
        var result = TryMesh(constrainedPolygon, true, quality);
        if (result != null) return result;

        // Tier 2: Non-conforming CDT + quality
        if (hasSegs)
        {
            result = TryMesh(constrainedPolygon, false, quality);
            if (result != null)
            {
                warning = "Using non-conforming CDT for tightly spaced constraints.";
                return result;
            }
        }

        // Tier 3: Conforming CDT, no quality
        if (hasQuality)
        {
            result = TryMesh(constrainedPolygon, true, null);
            if (result != null)
            {
                warning = "Quality constraints could not be applied.";
                return result;
            }
        }

        // Tier 4: Non-conforming CDT, no quality
        if (hasSegs)
        {
            result = TryMesh(constrainedPolygon, false, null);
            if (result != null)
            {
                warning = "Using non-conforming CDT without quality constraints.";
                return result;
            }
        }

        // Tier 5: Plain Delaunay (drop segments)
        result = TryMesh(unconstrainedPolygon, false, null);
        if (result != null)
        {
            warning = hasSegs
                ? "Constraints could not be enforced. Using plain Delaunay."
                : null;
            return result;
        }

        warning = "All triangulation attempts failed.";
        return null;
    }
}
