using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Baking must not change how anything looks.
///
/// Preview and bake used to read different tables — a pixel column of one and a millimetre column of
/// another — and preview fell back to a placeholder grey whenever the output layer was not in the
/// document yet. Since output layers are only created at bake time, that meant an un-baked terrain
/// drew grey and then changed colour the moment it was baked.
/// </summary>
public class PreviewBakeParityTests
{
    /// <summary>
    /// The headline case: with no layers in the document at all, preview still resolves each role's
    /// real colour rather than a placeholder.
    /// </summary>
    [RhinoNativeFact]
    public void BeforeAnyLayersExist_PreviewUsesTheTemplateColourNotAPlaceholder()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;

        foreach (LayerRole role in Enum.GetValues<LayerRole>())
        {
            LayerAppearance appearance = table.Appearance(role);
            var color = TerrainDisplayColors.Resolve(
                doc, table.Path(role), sourceLayerPath: null, colorArgb: null, appearance);

            Assert.Equal(appearance.ColorArgb, color.ToArgb());
        }
    }

    /// <summary>
    /// And once the layers exist, the colour has not moved — which is what "baking changes nothing"
    /// means in practice.
    /// </summary>
    [RhinoNativeFact]
    public void CreatingTheLayers_DoesNotChangeAnyPreviewColour()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;

        var before = Enum.GetValues<LayerRole>().ToDictionary(
            role => role,
            role => TerrainDisplayColors.Resolve(
                doc, table.Path(role), null, null, table.Appearance(role)).ToArgb());

        LayerCreationService.ApplyTemplate(doc, table);

        foreach (LayerRole role in Enum.GetValues<LayerRole>())
        {
            int after = TerrainDisplayColors.Resolve(
                doc, table.Path(role), null, null, table.Appearance(role)).ToArgb();

            Assert.Equal(before[role], after);
        }
    }

    /// <summary>
    /// Once a layer is in the document it belongs to the user, so the preview follows their edit
    /// rather than the template — the same rule the bake follows.
    /// </summary>
    [RhinoNativeFact]
    public void AUserEditedLayer_WinsOverTheTemplateInPreview()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;
        string path = table.Path(LayerRole.ContoursMajor);

        LayerCreationService.EnsureLayerPath(doc, path, table);
        int index = doc.Layers.FindByFullPath(path, -1);
        var layer = doc.Layers[index];
        layer.Color = System.Drawing.Color.HotPink;
        doc.Layers.Modify(layer, index, quiet: true);

        var color = TerrainDisplayColors.Resolve(
            doc, path, null, null, table.Appearance(LayerRole.ContoursMajor));

        Assert.Equal(System.Drawing.Color.HotPink.ToArgb(), color.ToArgb());
    }

    [Fact]
    public void AnExplicitObjectColour_AlwaysWins()
    {
        // Colour that carries data — an analysis ramp, a zone identity — is not the layer's to set.
        var color = TerrainDisplayColors.Resolve(
            doc: null!,
            layerPath: null,
            sourceLayerPath: null,
            colorArgb: unchecked((int)0xFF123456),
            LayerRoleTable.Default.Appearance(LayerRole.Terrain));

        Assert.Equal(unchecked((int)0xFF123456), color.ToArgb());
    }

    /// <summary>
    /// Preview thickness is derived from print width, so the screen shows the hierarchy the page
    /// does. There is one number per role now, not one for preview and another for print.
    /// </summary>
    [Fact]
    public void PreviewThickness_FollowsThePrintHierarchy()
    {
        var table = LayerRoleTable.Default;

        int sections = table.Appearance(LayerRole.Sections).PreviewWidthPx;
        int existing = table.Appearance(LayerRole.SectionsExisting).PreviewWidthPx;
        int grid = table.Appearance(LayerRole.SectionsGrid).PreviewWidthPx;

        // The proposed profile is the subject of the drawing; grid lines are the faintest thing on it.
        Assert.True(sections > existing);
        Assert.True(existing > grid);
    }

    /// <summary>
    /// The preview line weight multiplier is the one deliberate divergence, and it must stay
    /// display-only: it scales what the conduit draws and never reaches a layer or an object.
    /// </summary>
    [Fact]
    public void PreviewLineWeight_ScalesTheScreenOnly()
    {
        LayerAppearance appearance = LayerRoleTable.Default.Appearance(LayerRole.Sections);

        Assert.Equal(appearance.PreviewWidthPx, appearance.ScalePreviewWidth(1.0));
        Assert.True(appearance.ScalePreviewWidth(2.0) > appearance.PreviewWidthPx);

        // Scaling the screen must not have moved the printed width.
        Assert.Equal(0.70, appearance.PlotWeight);
    }
}
