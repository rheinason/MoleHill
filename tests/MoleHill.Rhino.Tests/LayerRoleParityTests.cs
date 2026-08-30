using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Temporary scaffolding for the layer-role migration: proves the new registry reproduces the two
/// tables it replaces — <c>GeneratedLayerDefaults</c>' suffix table and <c>SectionOutputLayers</c>'
/// kind-to-suffix switch — before either is deleted.
///
/// Delete this file together with those two classes. Its permanent replacements are
/// <see cref="LayerRoleRegistryTests"/> and <see cref="LayerRoleTableTests"/>, which assert the
/// behaviour we want rather than the behaviour we had.
/// </summary>
public class LayerRoleParityTests
{
    private static readonly LayerRoleTable Table = LayerRoleTable.Default;

    /// <summary>
    /// The eight section sublayers keep the exact paths the old kind-to-suffix switch produced.
    /// One test rather than a theory because <c>SectionLayerKind</c> is internal.
    /// </summary>
    [Fact]
    public void SectionRoles_ResolveToTheSamePathsAsTheOldSwitch()
    {
        var pairs = new (LayerRole Role, SectionLayerKind Kind)[]
        {
            (LayerRole.Sections, SectionLayerKind.Profile),
            (LayerRole.SectionsExisting, SectionLayerKind.ExistingProfile),
            (LayerRole.SectionsCuts, SectionLayerKind.Cuts),
            (LayerRole.SectionsGrid, SectionLayerKind.Grid),
            (LayerRole.SectionsTicks, SectionLayerKind.Ticks),
            (LayerRole.SectionsLabels, SectionLayerKind.Labels),
            (LayerRole.SectionsCutFillCut, SectionLayerKind.CutFillCut),
            (LayerRole.SectionsCutFillFill, SectionLayerKind.CutFillFill)
        };

        foreach (var (role, kind) in pairs)
        {
            string legacy = SectionOutputLayers.ResolveLayerPath(
                outputLayerPath: null,
                fallbackLayerPath: TerrainDefinition.DefaultAnnotationLayerPath,
                kind)!;

            Assert.Equal(legacy, Table.Path(role));
        }
    }

    [Theory]
    [InlineData(LayerRole.ContoursMajor, "::Contours::Major")]
    [InlineData(LayerRole.ContoursMinor, "::Contours::Minor")]
    [InlineData(LayerRole.Waterflow, "::Waterflow")]
    [InlineData(LayerRole.Labels, "::Labels")]
    [InlineData(LayerRole.Ticks, "::Ticks")]
    [InlineData(LayerRole.Grid, "::Grid")]
    [InlineData(LayerRole.Cuts, "::Cuts")]
    [InlineData(LayerRole.CutFillCut, "::CutFill::Cut")]
    [InlineData(LayerRole.CutFillFill, "::CutFill::Fill")]
    public void AnnotationRoles_LandOnTheSuffixesTheOldTableStyled(LayerRole role, string suffix)
    {
        Assert.Equal(TerrainDefinition.DefaultAnnotationLayerPath + suffix, Table.Path(role));
    }

    /// <summary>
    /// Print width and preview thickness are what the old table actually drove, so parity here is
    /// what guarantees no drawing changes weight or colour when the table is deleted.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRoles))]
    public void EveryRole_KeepsItsPrintWidthAndPreviewThickness(LayerRole role)
    {
        string path = Table.Path(role);
        var appearance = Table.Appearance(role);

        Assert.Equal(GeneratedLayerDefaults.GetPlotWeight(path), appearance.PlotWeight);
        Assert.Equal(GeneratedLayerDefaults.GetPreviewWidth(path), appearance.PreviewWidthPx);
    }

    [Theory]
    [MemberData(nameof(AllRoles))]
    public void EveryRole_KeepsTheColourTheOldTableSeeded(LayerRole role)
    {
        int? legacy = GeneratedLayerDefaults.GetColorArgb(Table.Path(role));
        if (legacy.HasValue)
            Assert.Equal(legacy.Value, Table.Appearance(role).ColorArgb);
    }

    public static TheoryData<LayerRole> AllRoles
    {
        get
        {
            var data = new TheoryData<LayerRole>();
            // Roles the old suffix table never knew about: markers and scatter had no layer at all,
            // and the roots are user-chosen paths it deliberately never restyled.
            var untabled = new[]
            {
                LayerRole.Terrain, LayerRole.Auxiliary, LayerRole.Annotation, LayerRole.Zones,
                LayerRole.Scatter, LayerRole.Walls, LayerRole.GradingAuxiliary,
                LayerRole.Markers, LayerRole.MarkerLabels, LayerRole.Contours,
                LayerRole.CutFill, LayerRole.SectionsCutFill
            };

            foreach (LayerRole role in Enum.GetValues<LayerRole>())
            {
                if (!untabled.Contains(role))
                    data.Add(role);
            }

            return data;
        }
    }
}
