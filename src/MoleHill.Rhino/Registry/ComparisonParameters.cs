using MoleHill.Rhino.Model;
using AnalysisParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.AnalysisDefinition>;
using AnnotationParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.AnnotationDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// The comparison rows Earthworks, Cut / Fill and the section cards declare (design review Steps 29, 66,
/// 68): one Compare To choice, then only the picker the chosen mode needs. Declared once here so the
/// families cannot drift; <see cref="ComparisonModes"/> reads and switches the mode.
/// </summary>
internal static class ComparisonParameters
{
    /// <summary>The section cards' drawn-profile list, which the panel builds in this position.</summary>
    public const string SectionProfilesKey = "SectionProfiles";

    /// <summary>
    /// Names a terrain for a summary ("Existing Ground"). Set by the panel, which can see the document's
    /// terrains; null in tests, where summaries fall back to "Another terrain".
    /// </summary>
    public static Func<Guid, string?>? TerrainName { get; set; }

    private static readonly IReadOnlyDictionary<string, string> ModeHelp = new Dictionary<string, string>
    {
        [nameof(ComparisonMode.Off)] = "Draw no reference and no cut/fill shading.",
        [nameof(ComparisonMode.Original)] = "This terrain's own surface before its modifiers moved it.",
        [nameof(ComparisonMode.Terrain)] = "Another MoleHill terrain in this document, as it is built.",
        [nameof(ComparisonMode.Geometry)] = "Rhino geometry: a survey mesh or surface.",
    };

    private static readonly RhinoObjectType GeometryTypes =
        RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion;

    private static ReferenceComparisonAnalysisDefinition Compared(AnalysisDefinition a) => (ReferenceComparisonAnalysisDefinition)a;

    private static TerrainSectionAnnotationDefinitionBase Section(AnnotationDefinition a) => (TerrainSectionAnnotationDefinitionBase)a;

    /// <summary>Earthworks' and Cut / Fill's rows: Original, Terrain or Geometry as segments.</summary>
    public static AnalysisParam[] Analysis() => new[]
    {
        AnalysisParam.Choice(
            "CompareTo", "Compare To",
            new[]
            {
                (nameof(ComparisonMode.Original), "Original"),
                (nameof(ComparisonMode.Terrain), "Terrain"),
                (nameof(ComparisonMode.Geometry), "Geometry"),
            },
            a => ComparisonModes.Of(Compared(a)).ToString(),
            (a, v) => ComparisonModes.Set(Compared(a), Enum.Parse<ComparisonMode>(v!)),
            "What cut and fill are measured against.",
            optionHelp: ModeHelp),
        AnalysisParam.TerrainReference(
            "ReferenceTerrain", "Terrain",
            a => Compared(a).ReferenceTerrainId,
            (a, id) => Compared(a).ReferenceTerrainId = id,
            "Another MoleHill terrain's finished mesh, compared against directly with no need to bake it.",
            meanwhile: "cut and fill are measured against this terrain's own base triangulation instead",
            visibleWhen: a => ComparisonModes.Of(Compared(a)) == ComparisonMode.Terrain),
        AnalysisParam.Sources(
            "Reference", "Geometry",
            a => Compared(a).Reference,
            GeometryTypes,
            "Existing ground as Rhino geometry: a survey mesh or surface.",
            visibleWhen: a => ComparisonModes.Of(Compared(a)) == ComparisonMode.Geometry),
    };

    /// <summary>
    /// The section cards' closed Comparison section: the mode (Off as well), its picker, the shading for a
    /// drawn reference, and the other terrains drawn as profiles. The summary names the reference.
    /// </summary>
    public static AnnotationParam[] Sections() => new[]
    {
        AnnotationParam.Section("comparison", "Comparison", summary: a => Summary(Section(a))),
        AnnotationParam.Choice(
            "CompareTo", "Compare To",
            new[]
            {
                (nameof(ComparisonMode.Off), "Off"),
                (nameof(ComparisonMode.Original), "Original Terrain"),
                (nameof(ComparisonMode.Terrain), "Another Terrain"),
                (nameof(ComparisonMode.Geometry), "Rhino Geometry"),
            },
            a => ComparisonModes.Of(Section(a)).ToString(),
            (a, v) => ComparisonModes.Set(Section(a), Enum.Parse<ComparisonMode>(v!)),
            "What this section is compared against: nothing, this terrain before its modifiers, another " +
            "terrain, or Rhino geometry. Off draws no reference and no cut/fill shading."),
        AnnotationParam.TerrainReference(
            "CutFillReferenceTerrain", "Terrain",
            a => Section(a).CutFillReferenceTerrainId,
            (a, id) => ComparisonModes.SetSectionTerrain(Section(a), id),
            "The terrain to draw on this section as existing ground, and to shade cut and fill against.",
            meanwhile: "its profile and the cut/fill shading are left off this section",
            visibleWhen: a => ComparisonModes.Of(Section(a)) == ComparisonMode.Terrain),
        AnnotationParam.Sources(
            "CutFillReference", "Geometry",
            a => Section(a).CutFillReference,
            GeometryTypes,
            "Existing ground as Rhino geometry, a survey mesh or surface, sliced along the same section line.",
            visibleWhen: a => ComparisonModes.Of(Section(a)) == ComparisonMode.Geometry),
        // Shading compares against a drawn reference; against the original surface it is the comparison itself.
        AnnotationParam.Bool(
            "ShowCutFillRegions", "Shade Cut / Fill",
            a => Section(a).ShowCutFillRegions,
            (a, v) => Section(a).ShowCutFillRegions = v,
            "Shade cut and fill between this terrain and the reference. Off draws the reference profile only.",
            visibleWhen: a => ComparisonModes.Of(Section(a)) is ComparisonMode.Terrain or ComparisonMode.Geometry),
        AnnotationParam.Placeholder(SectionProfilesKey, "Profiles"),
    };

    private static string Summary(TerrainSectionAnnotationDefinitionBase section)
    {
        ComparisonMode mode = ComparisonModes.Of(section);
        string reference = mode switch
        {
            ComparisonMode.Off => "Off",
            ComparisonMode.Geometry => "Rhino geometry",
            ComparisonMode.Terrain => section.CutFillReferenceTerrainId is { } id
                ? TerrainName?.Invoke(id) ?? "Another terrain"
                : "Another terrain",
            _ => "Original terrain",
        };
        return mode != ComparisonMode.Off && section.ShowCutFillRegions ? SummaryText.Join(reference, "cut/fill") : reference;
    }
}
