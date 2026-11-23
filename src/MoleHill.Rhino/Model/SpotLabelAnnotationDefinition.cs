using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

/// <summary>
/// A label at a spot on the terrain, placed at points, along curves or on a grid. Spot Heights and Spot
/// Slope share everything but what they measure. Each placement keeps its own sources, so switching
/// Points → Grid → Points gives back the points rather than reading them as boundaries; only the active
/// placement's sources are built, but every set is the card's data (renamed with its layers, carried by
/// export), so <see cref="EnumerateSourceSets"/> yields all three.
/// </summary>
public abstract class SpotLabelAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    public SpotPlacement Placement { get; set; } = SpotPlacement.Points;

    // Points placement reads the inherited Sources: Spot Heights (Points) cards kept their sources there,
    // so those documents migrate without moving them.

    /// <summary>Curves sampled at regular stations (Curves placement).</summary>
    public SourceReferenceSet CurveSources { get; set; } = new();

    /// <summary>Closed plan curves limiting the grid (Grid placement); empty covers the whole terrain.</summary>
    public SourceReferenceSet Boundaries { get; set; } = new();

    /// <summary>Distance along a curve between stations (Curves placement).</summary>
    [ModelLength]
    public double Interval { get; set; } = 10.0;

    /// <summary>Spacing of the plan grid (Grid placement).</summary>
    [ModelLength]
    public double GridSpacing { get; set; } = 5.0;

    /// <summary>The sources the active placement builds from.</summary>
    public SourceReferenceSet ActiveSources => Placement switch
    {
        SpotPlacement.Curves => CurveSources,
        SpotPlacement.Grid => Boundaries,
        _ => Sources,
    };

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
        yield return CurveSources;
        yield return Boundaries;
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        CurveSources ??= new SourceReferenceSet();
        Boundaries ??= new SourceReferenceSet();
        if (!Enum.IsDefined(Placement))
            Placement = SpotPlacement.Points;
        Interval = Interval > 0.0 ? Interval : unitContext.FromMeters(10.0);
        GridSpacing = GridSpacing > 0.0 ? GridSpacing : unitContext.FromMeters(5.0);
    }
}
