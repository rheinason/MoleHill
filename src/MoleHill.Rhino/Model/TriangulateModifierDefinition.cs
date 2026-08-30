namespace MoleHill.Rhino.Model;

public sealed class TriangulateModifierDefinition : GeometryInputModifierDefinition
{
    public const string AutoContourMode = "auto";
    public const string ConstrainedContourMode = "constrained";
    public const string VerticesOnlyContourMode = "vertices";
    public const int AutoUnconstrainedContourVertexThreshold = 250_000;

    public TriangulateModifierDefinition()
    {
        Label = "Triangulate";
    }

    /// <summary>
    /// Controls whether contour stations become constrained edges or ordinary TIN samples. Auto keeps
    /// exact contours for ordinary surveys and switches very large contour sets to vertex samples.
    /// Breaklines and the terrain boundary are always constrained.
    /// </summary>
    public string ContourMode { get; set; } = AutoContourMode;

    /// <summary>
    /// Planar Rhino surface carrying a numeric single-band GeoTIFF as its bitmap texture. Raster XY is
    /// mapped through the live surface, so moving the surface controls project/geographic placement.
    /// </summary>
    public SourceReferenceSet DemSurface { get; set; } = new();

    /// <summary>Raster elevation-unit to document-unit scale captured by the GeoTIFF import action.
    /// Zero asks snapshot capture to infer embedded units, falling back to document units.</summary>
    public double DemElevationScale { get; set; }

    public string? DemSourceFileName { get; set; }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        foreach (SourceReferenceSet sourceSet in base.EnumerateSourceSets())
            yield return sourceSet;
        yield return DemSurface;
    }

    public bool ShouldConstrainContours(int contourVertexCount)
    {
        if (string.Equals(ContourMode, VerticesOnlyContourMode, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(ContourMode, ConstrainedContourMode, StringComparison.OrdinalIgnoreCase))
            return true;

        return contourVertexCount < AutoUnconstrainedContourVertexThreshold;
    }
}
