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

    public bool ShouldConstrainContours(int contourVertexCount)
    {
        if (string.Equals(ContourMode, VerticesOnlyContourMode, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(ContourMode, ConstrainedContourMode, StringComparison.OrdinalIgnoreCase))
            return true;

        return contourVertexCount < AutoUnconstrainedContourVertexThreshold;
    }
}
