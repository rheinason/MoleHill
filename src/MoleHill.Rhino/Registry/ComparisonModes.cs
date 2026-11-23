using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>What a comparing card measures or draws against (design review Steps 29, 66, 68).</summary>
internal enum ComparisonMode
{
    /// <summary>Sections only: no reference drawn and no cut/fill shading.</summary>
    Off,

    /// <summary>This terrain's own surface before its modifiers moved it.</summary>
    Original,

    /// <summary>Another MoleHill terrain in the document.</summary>
    Terrain,

    /// <summary>Rhino geometry: a survey mesh or surface.</summary>
    Geometry,
}

/// <summary>
/// The comparison mode of Earthworks, Cut / Fill and the section cards, read from the data they already
/// save, so every existing card shows exactly the comparison it builds: geometry when Rhino geometry is
/// picked (it always took precedence), another terrain when one is chosen, otherwise the original surface,
/// or Off for a section that neither compares nor shades.
///
/// <para>Picking a mode clears the references the other modes use, so the build cannot silently fall back on
/// one the card no longer shows, and parks them for the session: switching back brings them back. A mode
/// chosen but not yet given its reference (Terrain with no terrain picked) is remembered for the session
/// so its picker stays on screen.</para>
/// </summary>
internal static class ComparisonModes
{
    private sealed record Parked(Guid? Terrain, List<Guid> Objects, List<string> Layers);

    private static readonly Dictionary<Guid, ComparisonMode> Pending = new();
    private static readonly Dictionary<Guid, Parked> ParkedReferences = new();

    public static ComparisonMode Of(ReferenceComparisonAnalysisDefinition analysis) =>
        Resolve(analysis.Id, analysis.Reference.HasReferences, analysis.ReferenceTerrainId.HasValue, shading: true, allowOff: false);

    public static ComparisonMode Of(TerrainSectionAnnotationDefinitionBase section) =>
        Resolve(section.Id, section.CutFillReference.HasReferences, section.CutFillReferenceTerrainId.HasValue, section.ShowCutFillRegions, allowOff: true);

    /// <summary>Switches an analysis to <paramref name="mode"/>; call inside the card's mutation.</summary>
    public static void Set(ReferenceComparisonAnalysisDefinition analysis, ComparisonMode mode) =>
        Apply(analysis.Id, mode, analysis.Reference, () => analysis.ReferenceTerrainId, id => analysis.ReferenceTerrainId = id, setShading: null);

    /// <summary>
    /// Switches a section to <paramref name="mode"/>; call inside the card's mutation.
    /// <paramref name="setTerrain"/> keeps the drawn-profile list in step with the compared terrain.
    /// Turning comparison on starts with cut/fill shading on (the approved default); Off turns it off.
    /// </summary>
    public static void Set(TerrainSectionAnnotationDefinitionBase section, ComparisonMode mode, Action<Guid?> setTerrain)
    {
        bool wasOff = Of(section) == ComparisonMode.Off;
        Apply(section.Id, mode, section.CutFillReference, () => section.CutFillReferenceTerrainId, setTerrain,
            shading =>
            {
                // Off turns shading off; turning comparison on starts with it on. Between two comparing
                // modes the user's own choice stands.
                if (!shading || wasOff)
                    section.ShowCutFillRegions = shading;
            });
    }

    public static void ResetForTests()
    {
        Pending.Clear();
        ParkedReferences.Clear();
    }

    private static ComparisonMode Resolve(Guid id, bool hasGeometry, bool hasTerrain, bool shading, bool allowOff)
    {
        ComparisonMode derived = hasGeometry ? ComparisonMode.Geometry
            : hasTerrain ? ComparisonMode.Terrain
            : allowOff && !shading ? ComparisonMode.Off
            : ComparisonMode.Original;

        if (Pending.TryGetValue(id, out ComparisonMode pending))
        {
            // A chosen mode waiting for its reference shows until the reference arrives or another mode is picked.
            if (pending is ComparisonMode.Terrain or ComparisonMode.Geometry && derived is ComparisonMode.Original or ComparisonMode.Off)
                return pending;

            Pending.Remove(id);
        }

        return derived;
    }

    private static void Apply(
        Guid id,
        ComparisonMode mode,
        SourceReferenceSet geometry,
        Func<Guid?> getTerrain,
        Action<Guid?> setTerrain,
        Action<bool>? setShading)
    {
        // Park what is set now, merged with anything parked earlier, so no reference is lost by switching.
        ParkedReferences.TryGetValue(id, out Parked? parked);
        Guid? terrain = getTerrain() ?? parked?.Terrain;
        var objects = geometry.ObjectIds.Count > 0 ? geometry.ObjectIds.ToList() : parked?.Objects ?? new List<Guid>();
        var layers = geometry.LayerPaths.Count > 0 ? geometry.LayerPaths.ToList() : parked?.Layers ?? new List<string>();
        ParkedReferences[id] = new Parked(terrain, objects, layers);

        bool useTerrain = mode == ComparisonMode.Terrain;
        bool useGeometry = mode == ComparisonMode.Geometry;
        setTerrain(useTerrain ? terrain : null);
        geometry.ReplaceObjects(useGeometry ? objects : Array.Empty<Guid>());
        geometry.ReplaceLayers(useGeometry ? layers : Array.Empty<string>());
        setShading?.Invoke(mode != ComparisonMode.Off);

        if (useTerrain || useGeometry)
            Pending[id] = mode;
        else
            Pending.Remove(id);
    }
}
