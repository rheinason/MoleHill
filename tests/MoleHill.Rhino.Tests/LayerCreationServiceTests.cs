using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The two rules layer creation has to get right: appearance lands on the layer it was declared
/// for, and a layer that already exists belongs to the user.
/// </summary>
public class LayerCreationServiceTests
{
    /// <summary>
    /// Creating a deep path used to apply the leaf's appearance to every ancestor, so making
    /// <c>Annotation::Sections::CutFill::Fill</c> repainted <c>Annotation</c> and
    /// <c>Annotation::Sections</c> to look like the fill — and whichever child was written last won.
    /// </summary>
    [RhinoNativeFact]
    public void CreatingALeaf_DoesNotRestyleItsAncestors()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;

        LayerCreationService.EnsureLayerPath(doc, table.Path(LayerRole.SectionsCutFillFill), table);

        var fill = table.Appearance(LayerRole.SectionsCutFillFill);
        var sections = table.Appearance(LayerRole.Sections);
        var annotation = table.Appearance(LayerRole.Annotation);

        Assert.Equal(fill.ColorArgb, ColorOf(doc, table.Path(LayerRole.SectionsCutFillFill)));
        Assert.Equal(sections.ColorArgb, ColorOf(doc, table.Path(LayerRole.Sections)));
        Assert.Equal(annotation.ColorArgb, ColorOf(doc, table.Path(LayerRole.Annotation)));
    }

    /// <summary>
    /// Seed once, then the layer wins. Rebuilding a terrain must not undo a colour someone chose in
    /// Rhino's Layers panel — which the old template apply did on every run.
    /// </summary>
    [RhinoNativeFact]
    public void AnExistingLayer_KeepsTheUsersAppearance()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;
        string path = table.Path(LayerRole.ContoursMajor);

        LayerCreationService.EnsureLayerPath(doc, path, table);
        SetColor(doc, path, System.Drawing.Color.HotPink);

        LayerCreationService.EnsureLayerPath(doc, path, table);
        LayerCreationService.ApplyTemplate(doc, table);

        Assert.Equal(System.Drawing.Color.HotPink.ToArgb(), ColorOf(doc, path));
    }

    [RhinoNativeFact]
    public void RestyleExisting_IsWhatPutsTheTemplateBack()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;
        string path = table.Path(LayerRole.ContoursMajor);

        LayerCreationService.EnsureLayerPath(doc, path, table);
        SetColor(doc, path, System.Drawing.Color.HotPink);

        LayerCreationService.ApplyTemplate(doc, table, restyleExisting: true);

        Assert.Equal(table.Appearance(LayerRole.ContoursMajor).ColorArgb, ColorOf(doc, path));
    }

    [RhinoNativeFact]
    public void ApplyTemplate_IsIdempotent()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;

        var first = LayerCreationService.ApplyTemplate(doc, table);
        var second = LayerCreationService.ApplyTemplate(doc, table);

        Assert.True(first.Created > 0);
        Assert.Equal(0, second.Created);
        Assert.Equal(table.AllLayers.Count, second.Existing);
    }

    /// <summary>
    /// Every role must end up on a real layer, because output whose layer cannot be resolved falls
    /// through to whatever layer the user happens to be working on.
    /// </summary>
    [RhinoNativeFact]
    public void EveryRole_ResolvesToALayerThatExistsAfterApply()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;

        LayerCreationService.ApplyTemplate(doc, table);

        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            Assert.True(doc.Layers.FindByFullPath(table.Path(role), -1) >= 0, $"Role {role} has no layer.");
    }

    [RhinoNativeFact]
    public void ASourceLayerColour_WinsOnTheLeafOnly()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        var table = LayerRoleTable.Default;

        int sourceIndex = LayerCreationService.EnsureLayerPath(doc, "Site::Lawn", table);
        SetColor(doc, "Site::Lawn", System.Drawing.Color.SeaGreen);

        string zonePath = table.Path(LayerRole.Zones, "Site::Lawn");
        LayerCreationService.EnsureLayerPath(doc, zonePath, table, doc.Layers[sourceIndex]);

        Assert.Equal(System.Drawing.Color.SeaGreen.ToArgb(), ColorOf(doc, zonePath));
        // The branch above it is MoleHill's own and must not take the zone's colour.
        Assert.NotEqual(System.Drawing.Color.SeaGreen.ToArgb(), ColorOf(doc, table.Path(LayerRole.Zones)));
    }

    private static int ColorOf(RhinoDoc doc, string fullPath)
    {
        int index = doc.Layers.FindByFullPath(fullPath, -1);
        Assert.True(index >= 0, $"Layer '{fullPath}' was not created.");
        return doc.Layers[index].Color.ToArgb();
    }

    private static void SetColor(RhinoDoc doc, string fullPath, System.Drawing.Color color)
    {
        int index = doc.Layers.FindByFullPath(fullPath, -1);
        var layer = doc.Layers[index];
        layer.Color = color;
        doc.Layers.Modify(layer, index, quiet: true);
    }
}
