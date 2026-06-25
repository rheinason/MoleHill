using MoleHill.Core.Scattering;

namespace MoleHill.Rhino.Model;

/// <summary>
/// Generates a field of randomized block instances across the terrain inside one or more boundary
/// regions — a light object-scatter (Blender / ScatterForest style). Unlike the other object
/// definitions it does not re-orient existing objects; it emits new instances that the display conduit
/// previews and bake materialises. Reuses the base random rotation/scale/seed/Z-offset.
/// </summary>
public sealed class ScatterObjectDefinition : TerrainObjectDefinition
{
    public ScatterObjectDefinition()
    {
        Name = "Scatter";
    }

    /// <summary>Boundary region(s) to fill (closed planar curves and/or layers; multiple accepted).</summary>
    public SourceReferenceSet Boundaries { get; set; } = new();

    /// <summary>Weighted mix of blocks to scatter.</summary>
    public List<ScatterBlockEntry> Blocks { get; set; } = new();

    public ScatterPattern Pattern { get; set; } = ScatterPattern.Random;

    public ScatterDensityMode DensityMode { get; set; } = ScatterDensityMode.Count;

    public double Count { get; set; } = 100.0;

    public double PerAreaDensity { get; set; } = 0.1;

    public double Spacing { get; set; } = 1.0;

    public bool SlopeFilterEnabled { get; set; }

    public double SlopeMinDegrees { get; set; }

    public double SlopeMaxDegrees { get; set; } = 90.0;

    public bool ElevationFilterEnabled { get; set; }

    public double ElevationMin { get; set; }

    public double ElevationMax { get; set; }

    /// <summary>Orient instances to the terrain normal; when false they stay upright (world Z).</summary>
    public bool AlignToSlope { get; set; }

    public ScatterPreviewMode PreviewMode { get; set; } = ScatterPreviewMode.ShapePoints;

    public int PreviewCap { get; set; } = 2000;

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
        yield return Boundaries;
        foreach (var entry in Blocks)
            yield return entry.Source;
    }
}
