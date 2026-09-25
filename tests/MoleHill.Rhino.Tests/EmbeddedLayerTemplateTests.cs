using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A document carries its own copy of the templates it uses, so it renders the same wherever it is
/// opened rather than picking up whatever the opener happens to have installed.
/// </summary>
[Collection(nameof(LayerTemplateProviderCollection))]
public class EmbeddedLayerTemplateTests : IDisposable
{
    private readonly Func<IReadOnlyList<LayerTemplateDefinition>>? _previousProvider =
        LayerRoleService.TemplateProvider;

    public void Dispose()
    {
        LayerRoleService.TemplateProvider = _previousProvider;
        LayerRoleService.Invalidate();
    }

    private static LayerTemplateDefinition Office(string annotationPath) => new()
    {
        Version = 1,
        Name = "Office",
        Entries = new List<LayerTemplateEntry>
        {
            new() { Roles = { "annotation" }, Path = annotationPath }
        }
    };

    /// <summary>
    /// The point of embedding: after the document has its copy, changing what is installed locally
    /// must not move the drawing.
    /// </summary>
    [RhinoNativeFact]
    public void TheEmbeddedCopy_WinsOverTheLocalTemplate()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::AsSaved") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);

        Assert.Equal("Drawing::AsSaved", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));

        // Someone else's machine, with a different standard installed under the same name.
        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::TheirStandard") };
        LayerRoleService.Invalidate();

        Assert.Equal("Drawing::AsSaved", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));
    }

    [RhinoNativeFact]
    public void ADocumentWithNoCopy_IsSeededFromTheLocalTemplate()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Seeded") };
        LayerRoleService.Invalidate();

        Assert.Null(LayerTemplateDocumentStore.Load(doc));
        LayerRoleService.EnsureTemplateLayers(doc);

        Assert.NotNull(LayerTemplateDocumentStore.Load(doc));
        Assert.Equal("Drawing::Seeded", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));
    }

    /// <summary>
    /// Divergence is reported, never resolved on its own — only the user can say which side wins.
    /// </summary>
    [RhinoNativeFact]
    public void WhenTheTwoAreEditedApart_DivergenceIsReportedAndNeitherSideMoves()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Original") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);

        Assert.False(LayerRoleService.DivergesFromLocal(doc));

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Revised") };
        LayerRoleService.Invalidate();

        Assert.True(LayerRoleService.DivergesFromLocal(doc));
        Assert.Equal("Drawing::Original", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));
    }

    [RhinoNativeFact]
    public void PullingFromLocal_ReplacesTheDocumentCopy()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Original") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Revised") };
        LayerRoleService.Invalidate();

        Assert.True(LayerRoleService.PullFromLocal(doc, "Office"));
        Assert.Equal("Drawing::Revised", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));
        Assert.False(LayerRoleService.DivergesFromLocal(doc));
    }

    /// <summary>
    /// Saving in the template editor must reach the document it was opened from, even though that
    /// document already carries its own copy — otherwise the edit never shows.
    /// </summary>
    [RhinoNativeFact]
    public void PullEditedFromLocal_EditedTemplate_ReplacesTheDocumentCopy()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Original") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);
        Assert.Equal("Drawing::Original", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));

        var saved = new[] { Office("Drawing::Edited") };
        LayerRoleService.TemplateProvider = () => saved;

        Assert.Equal(1, LayerRoleService.PullEditedFromLocal(doc, saved));
        Assert.Equal("Drawing::Edited", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));
        Assert.False(LayerRoleService.DivergesFromLocal(doc));
    }

    /// <summary>
    /// The sync touches only templates the document carries and never changes which one is active:
    /// a template the document does not embed is not added.
    /// </summary>
    [RhinoNativeFact]
    public void PullEditedFromLocal_TemplateNotEmbedded_LeavesTheDocumentAlone()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Original") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);

        var other = new LayerTemplateDefinition
        {
            Version = 1,
            Name = "Other",
            Entries = new List<LayerTemplateEntry> { new() { Roles = { "annotation" }, Path = "Drawing::Other" } }
        };

        Assert.Equal(0, LayerRoleService.PullEditedFromLocal(doc, new[] { Office("Drawing::Original"), other }));
        EmbeddedLayerTemplateState state = LayerTemplateDocumentStore.Load(doc)!;
        Assert.Single(state.Templates);
        Assert.Equal("Office", state.ActiveName);
    }

    /// <summary>
    /// A document from another office, whose template is not installed here, is not divergent — it
    /// simply has nothing to compare against, and must keep rendering as saved.
    /// </summary>
    [RhinoNativeFact]
    public void ATemplateNotInstalledLocally_IsNotTreatedAsDivergent()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Original") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);

        LayerRoleService.TemplateProvider = () => Array.Empty<LayerTemplateDefinition>();
        LayerRoleService.Invalidate();

        Assert.False(LayerRoleService.DivergesFromLocal(doc));
        Assert.Equal("Drawing::Original", LayerRoleService.GetTable(doc).Path(LayerRole.Annotation));
    }

    /// <summary>
    /// Two terrains can be drawn on separate layers by naming different templates — what the
    /// per-terrain output layer paths used to provide.
    /// </summary>
    [RhinoNativeFact]
    public void ATerrain_CanNameADifferentTemplate()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[]
        {
            Office("Drawing::Proposed"),
            new LayerTemplateDefinition
            {
                Version = 1,
                Name = "Existing",
                Entries = new List<LayerTemplateEntry> { new() { Roles = { "annotation" }, Path = "Drawing::Existing" } }
            }
        };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);
        LayerTemplateDocumentStore.Embed(
            doc,
            LayerRoleService.TemplateProvider!()[1],
            localFingerprint: 0,
            makeActive: false);
        LayerRoleService.Invalidate();

        var proposed = new TerrainDefinition { Name = "Proposed" };
        var existing = new TerrainDefinition { Name = "Existing", LayerTemplateName = "Existing" };

        Assert.Equal("Drawing::Proposed", LayerRoleService.GetTable(doc, proposed).Path(LayerRole.Annotation));
        Assert.Equal("Drawing::Existing", LayerRoleService.GetTable(doc, existing).Path(LayerRole.Annotation));
    }

    /// <summary>
    /// Layers are ensured per template, not once per document: a second terrain naming a different
    /// template must find its own layers created before it previews, or preview and bake read
    /// different layer appearance.
    /// </summary>
    [RhinoNativeFact]
    public void EnsureTemplateLayers_SecondTerrainWithOtherTemplate_CreatesItsLayers()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        var existingTemplate = new LayerTemplateDefinition
        {
            Version = 1,
            Name = "Existing",
            Entries = new List<LayerTemplateEntry> { new() { Roles = { "annotation" }, Path = "Drawing::Existing" } }
        };
        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Proposed") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc, new TerrainDefinition { Name = "Proposed" });
        LayerTemplateDocumentStore.Embed(doc, existingTemplate, localFingerprint: 0, makeActive: false);

        LayerRoleService.EnsureTemplateLayers(doc, new TerrainDefinition { Name = "Existing", LayerTemplateName = "Existing" });

        Assert.True(doc.Layers.FindByFullPath("Drawing::Proposed", -1) >= 0);
        Assert.True(doc.Layers.FindByFullPath("Drawing::Existing", -1) >= 0);
    }

    /// <summary>A terrain naming a template the document does not carry keeps routing, rather than
    /// losing its layers because a name went stale.</summary>
    [RhinoNativeFact]
    public void ATerrainNamingAMissingTemplate_FallsBackToTheActiveOne()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);

        LayerRoleService.TemplateProvider = () => new[] { Office("Drawing::Active") };
        LayerRoleService.Invalidate();
        LayerRoleService.EnsureTemplateLayers(doc);

        var terrain = new TerrainDefinition { LayerTemplateName = "Deleted" };
        Assert.Equal("Drawing::Active", LayerRoleService.GetTable(doc, terrain).Path(LayerRole.Annotation));
    }
}

/// <summary>
/// These tests swap the process-wide template provider, so they must not run beside each other.
/// </summary>
[CollectionDefinition(nameof(LayerTemplateProviderCollection), DisableParallelization = true)]
public sealed class LayerTemplateProviderCollection
{
}
