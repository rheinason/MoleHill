namespace MoleHill.Rhino.Model;

/// <summary>
/// The closed set of destinations MoleHill generates output for.
///
/// A role is the stable answer to "where does this kind of output go, and what does it look like" —
/// the layer path and the appearance both hang off it, and both are declared once in
/// <see cref="MoleHill.Rhino.Registry.LayerRoleRegistry"/> and bound to real layers by the active
/// layer template. Nothing in the build pipeline may hardcode or plumb a layer path.
///
/// Public because <see cref="LayerTemplateEntry.Role"/> serializes the id, not this enum: renaming a
/// member here is safe, but renaming its registry id breaks every saved template.
/// </summary>
public enum LayerRole
{
    // ── Roots ────────────────────────────────────────────────────────────────
    Terrain,
    Auxiliary,
    Annotation,

    /// <summary>Zone meshes. A prefix role: the zone's input layer path is appended to it.</summary>
    Zones,

    Scatter,

    // ── Auxiliary family ─────────────────────────────────────────────────────
    Walls,
    GradingAuxiliary,

    // ── Drawing families under Annotation ────────────────────────────────────
    Contours,
    ContoursMajor,
    ContoursMinor,

    /// <summary>Contours of a cut/fill delta — depths, not elevations.</summary>
    CutFillContours,

    /// <summary>Where the cut/fill delta crosses zero: the line cut and fill balance along.</summary>
    BalanceLine,

    Waterflow,

    /// <summary>Catchment boundary polygons — the divides between drainage basins.</summary>
    Catchments,

    /// <summary>A catchment's longest downhill flow path, from its high point to its outlet.</summary>
    CatchmentFlowPaths,

    /// <summary>Shorelines of closed depressions, at the level each overflows at.</summary>
    Ponding,

    /// <summary>Markers where a depression overflows.</summary>
    PondingSpillPoints,

    Labels,

    /// <summary>Marker block instances.</summary>
    Markers,

    /// <summary>Marker value text. Shares the marker layer unless bound separately.</summary>
    MarkerLabels,

    // ── Section drawing family ───────────────────────────────────────────────
    /// <summary>The proposed profile — the subject of a section drawing.</summary>
    Sections,
    SectionsExisting,
    SectionsCuts,
    SectionsGrid,
    SectionsTicks,
    SectionsLabels,
    SectionsCutFill,
    SectionsCutFillCut,
    SectionsCutFillFill
}
