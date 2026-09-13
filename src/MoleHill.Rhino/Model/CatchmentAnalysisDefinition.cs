namespace MoleHill.Rhino.Model;

/// <summary>
/// Which ground drains to which outlet: the terrain split into catchments, coloured on the preview and
/// drawn as boundary polygons.
/// </summary>
public sealed class CatchmentAnalysisDefinition : DrainageAnalysisDefinition
{
    /// <summary>
    /// Catchments smaller than this share of the terrain are absorbed into the one they spill into.
    ///
    /// A percentage, not an area, and that is a deliberate choice rather than a shortcut. A share is
    /// scale-free — the same 1% is right on a housing plot and on a quarry — where an absolute area would
    /// have to be retyped for every site. It also keeps the row honest: this codebase does not show
    /// unlabelled numbers, and there is no model-area unit to label one with.
    ///
    /// Zero keeps every catchment, which on a real survey means hundreds of slivers along the low edge.
    /// </summary>
    public double MinimumBasinAreaPercent { get; set; } = 1.0;

    /// <summary>Draw the catchment boundaries as closed polygons.</summary>
    public bool ShowBoundaries { get; set; } = true;

    /// <summary>
    /// Draw each catchment's longest flow path — from its high point down to its outlet, traced the same
    /// way the Waterflow analysis traces, so the two features draw the same kind of line.
    /// </summary>
    public bool ShowFlowPaths { get; set; }

    /// <summary>Explicit colour for the boundaries. Null takes the colour from the role's layer.</summary>
    public int? BoundaryColorArgb { get; set; }

    /// <summary>Explicit colour for the flow paths. Null takes the colour from the role's layer.</summary>
    public int? FlowPathColorArgb { get; set; }

    public CatchmentAnalysisDefinition()
    {
        Label = "Catchments";
    }
}
