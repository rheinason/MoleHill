using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Engine;

[Flags]
public enum TriangulationWarningFlags
{
    None = 0,
    UsedNonConformingCdt = 1 << 0,
    DroppedQualityConstraints = 1 << 1,
    DroppedSegments = 1 << 2,
    UsedPlainDelaunayFallback = 1 << 3
}

public sealed class TriangulationOutcome
{
    public IMesh? Mesh { get; init; }

    public string? WarningMessage { get; init; }

    public TriangulationWarningFlags Flags { get; init; }

    public IReadOnlyList<string> FailureDetails { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Shared triangulation with multi-tier fallback.
/// Used by TinEngine, PadGrader, RemeshComponent, MeshAreaSplitter.
/// </summary>
public static class TriangulationHelper
{
    private static readonly object TriangulateLock = new();

    /// <summary>
    /// Triangulate vertices + segments with automatic fallback:
    /// 1. Conforming CDT + quality
    /// 2. Non-conforming CDT + quality
    /// 3. Conforming CDT, no quality
    /// 4. Non-conforming CDT, no quality
    /// 5. Plain Delaunay (drops segments)
    /// </summary>
    public static TriangulationOutcome Triangulate(
        List<double> xyList, int vertexCount,
        List<(int a, int b)> segments,
        double maxArea, double minAngle,
        bool convex = true,
        int segmentSplitting = 0)
    {
        bool hasSegs = segments.Count > 0;
        bool hasQuality = maxArea > 0 || minAngle > 0;
        var mesher = new GenericMesher();
        var failureDetails = new List<string>(5);

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

        static string FormatFailure(string attemptName, Exception? ex, string? detail = null)
        {
            if (!string.IsNullOrWhiteSpace(detail))
                return $"{attemptName}: {detail}";

            if (ex == null)
                return $"{attemptName}: failed.";

            return $"{attemptName}: {ex.GetType().Name}: {ex.Message}";
        }

        var constrainedPolygon = BuildPolygon(includeSegs: true);
        var unconstrainedPolygon = hasSegs ? BuildPolygon(includeSegs: false) : constrainedPolygon;

        QualityOptions? quality = null;
        if (hasQuality)
        {
            quality = new QualityOptions();
            if (maxArea > 0) quality.MaximumArea = maxArea;
            if (minAngle > 0) quality.MinimumAngle = minAngle;
            // Cap Steiner points to prevent runaway refinement.
            // Derive the cap from the expected output triangle count (bbox area / maxArea)
            // rather than input vertex count, since vertex count is uncorrelated with output size.
            // Without a cap, Triangle.NET can cascade indefinitely when quality options are active.
            int steinCap;
            if (maxArea > 0 && vertexCount > 0)
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                for (int i = 0; i < vertexCount; i++)
                {
                    double x = xyList[i * 2], y = xyList[i * 2 + 1];
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
                double bboxArea = (maxX - minX) * (maxY - minY);
                // Allow 3× the expected triangle count as a safety margin.
                int expectedTriangles = (int)Math.Min(3.0 * bboxArea / maxArea, 2_000_000.0);
                steinCap = Math.Max(expectedTriangles, vertexCount * 4);
            }
            else
            {
                // minAngle-only quality: bound loosely by vertex count
                steinCap = vertexCount * 10;
            }
            quality.SteinerPoints = Math.Min(steinCap, 2_000_000);
        }

        IMesh? TryMesh(Polygon poly, bool conforming, QualityOptions? qualityOptions, string attemptName)
        {
            var opts = new ConstraintOptions
            {
                // ConformingDelaunay inserts Steiner points along segments to enforce the
                // Voronoi/Delaunay property — independently of the QualityOptions.SteinerPoints cap.
                // With tight parallel constraints (e.g. road edges near a retaining wall) this
                // cascades indefinitely.  Only enable it when quality refinement is also active so
                // the SteinerPoints cap actually governs the conforming pass too.
                ConformingDelaunay = conforming && hasSegs && hasQuality,
                Convex = convex,
                SegmentSplitting = segmentSplitting
            };
            try
            {
                IMesh mesh;
                lock (TriangulateLock)
                {
                    mesh = mesher.Triangulate(poly, opts, qualityOptions);
                }
                if (mesh.Triangles.Count > 0)
                    return mesh;

                failureDetails.Add(FormatFailure(attemptName, null, "produced 0 triangles"));
                return null;
            }
            catch (Exception ex)
            {
                failureDetails.Add(FormatFailure(attemptName, ex));
                return null;
            }
        }

        // Tier 1: Conforming CDT + quality
        var result = TryMesh(constrainedPolygon, true, quality, "Conforming CDT + quality");
        if (result != null)
        {
            return new TriangulationOutcome
            {
                Mesh = result
            };
        }

        // Tier 2: Non-conforming CDT + quality
        if (hasSegs)
        {
            result = TryMesh(constrainedPolygon, false, quality, "Non-conforming CDT + quality");
            if (result != null)
            {
                return new TriangulationOutcome
                {
                    Mesh = result,
                    WarningMessage = "Using non-conforming CDT for tightly spaced constraints.",
                    Flags = TriangulationWarningFlags.UsedNonConformingCdt
                };
            }
        }

        // Tier 3: Conforming CDT, no quality
        if (hasQuality)
        {
            result = TryMesh(constrainedPolygon, true, null, "Conforming CDT without quality");
            if (result != null)
            {
                return new TriangulationOutcome
                {
                    Mesh = result,
                    WarningMessage = "Quality constraints could not be applied.",
                    Flags = TriangulationWarningFlags.DroppedQualityConstraints
                };
            }
        }

        // Tier 4: Non-conforming CDT, no quality
        if (hasSegs)
        {
            result = TryMesh(constrainedPolygon, false, null, "Non-conforming CDT without quality");
            if (result != null)
            {
                return new TriangulationOutcome
                {
                    Mesh = result,
                    WarningMessage = "Using non-conforming CDT without quality constraints.",
                    Flags = TriangulationWarningFlags.UsedNonConformingCdt | TriangulationWarningFlags.DroppedQualityConstraints
                };
            }
        }

        // Tier 5: Plain Delaunay (drop segments)
        result = TryMesh(unconstrainedPolygon, false, null, hasSegs ? "Plain Delaunay fallback" : "Delaunay");
        if (result != null)
        {
            TriangulationWarningFlags flags = TriangulationWarningFlags.None;
            if (hasSegs)
                flags |= TriangulationWarningFlags.DroppedSegments | TriangulationWarningFlags.UsedPlainDelaunayFallback;
            if (hasQuality)
                flags |= TriangulationWarningFlags.DroppedQualityConstraints;

            return new TriangulationOutcome
            {
                Mesh = result,
                WarningMessage = hasSegs
                    ? "Constraints could not be enforced. Using plain Delaunay."
                    : null,
                Flags = flags
            };
        }

        string? failureSummary = failureDetails.Count > 0
            ? string.Join(" | ", failureDetails)
            : null;
        return new TriangulationOutcome
        {
            WarningMessage = failureSummary == null
                ? "All triangulation attempts failed."
                : $"All triangulation attempts failed. {failureSummary}",
            FailureDetails = failureDetails
        };
    }
}
