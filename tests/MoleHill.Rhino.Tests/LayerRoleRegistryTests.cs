using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Invariants of the role table itself, independent of any template or document.
/// </summary>
public class LayerRoleRegistryTests
{
    [Fact]
    public void EveryRole_HasADescriptor()
    {
        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            Assert.NotNull(LayerRoleRegistry.For(role));
    }

    /// <summary>
    /// Role ids are a persisted contract: they are written into every saved layer template and into
    /// every document that embeds one. Renaming one silently unbinds that role everywhere, so the
    /// full set is spelled out here rather than derived — this test must break when an id changes.
    /// </summary>
    [Fact]
    public void RoleIds_AreTheDocumentedPersistedContract()
    {
        var expected = new[]
        {
            "terrain", "auxiliary", "annotation", "zones", "scatter",
            "walls", "grading-aux",
            "contours", "contours-major", "contours-minor",
            "cut-fill-contours", "balance-line",
            "waterflow", "catchments", "catchment-flow-paths",
            "ponding", "ponding-spill-points", "labels",
            "markers", "marker-labels",
            "sections", "sections-existing", "sections-cuts", "sections-grid", "sections-ticks",
            "sections-labels", "sections-cutfill", "sections-cutfill-cut", "sections-cutfill-fill"
        };

        Assert.Equal(expected.OrderBy(id => id), LayerRoleRegistry.All.Select(d => d.Id).OrderBy(id => id));
    }

    [Fact]
    public void RoleIdsAndDefaultPaths_AreUnique()
    {
        var ids = LayerRoleRegistry.All.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Two roles may deliberately share a layer (marker labels sit on the marker layer), but only
        // when one is the other's parent with an empty suffix.
        var sharing = LayerRoleRegistry.All
            .GroupBy(d => LayerRoleRegistry.DefaultPath(d.Role), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in sharing)
        {
            // Exactly one role owns the layer; the others sit on it by declaring no suffix of their
            // own, which is how marker labels share the marker layer until someone splits them out.
            Assert.Single(group, d => d.RelativeSuffix.Length > 0 || !d.Parent.HasValue);
        }
    }

    /// <summary>
    /// Every chain terminates at a root carrying an absolute path. This is what guarantees
    /// <see cref="LayerRoleTable.Path"/> can never return null, and therefore that no generated
    /// object can fall through to Rhino's current layer.
    /// </summary>
    [Fact]
    public void EveryChain_TerminatesAtARootWithAnAbsolutePath()
    {
        foreach (var descriptor in LayerRoleRegistry.All)
        {
            var current = descriptor;
            int guard = 0;
            while (current.Parent.HasValue)
            {
                Assert.Null(current.AbsoluteDefaultPath);
                current = LayerRoleRegistry.For(current.Parent.Value);
                Assert.True(++guard < 16, $"Role '{descriptor.Id}' has a cyclic parent chain.");
            }

            Assert.False(string.IsNullOrWhiteSpace(current.AbsoluteDefaultPath));
            Assert.False(string.IsNullOrWhiteSpace(LayerRoleRegistry.DefaultPath(descriptor.Role)));
        }
    }

    [Fact]
    public void DerivePreviewWidth_IsMonotonicInPrintWidth()
    {
        double[] weights = { 0.05, 0.13, 0.18, 0.30, 0.35, 0.50, 0.70, 1.2 };
        var widths = weights.Select(weight => LayerRoleRegistry.DerivePreviewWidthPx(weight)).ToArray();

        for (int i = 1; i < widths.Length; i++)
            Assert.True(widths[i] >= widths[i - 1], "A heavier print width must never preview thinner.");

        Assert.Equal(LayerRoleRegistry.DefaultPreviewWidthPx, LayerRoleRegistry.DerivePreviewWidthPx(null));
    }

    [Fact]
    public void CutAndFill_AreNeverSeededTheSameColour()
    {
        var table = LayerRoleTable.Default;

        Assert.NotEqual(
            table.Appearance(LayerRole.SectionsCutFillCut).ColorArgb,
            table.Appearance(LayerRole.SectionsCutFillFill).ColorArgb);
    }

    /// <summary>
    /// Drawing output is ByLayer so the office layer table governs the print; output whose colour
    /// is data cannot be. Pinned because getting this wrong is invisible until something prints.
    /// </summary>
    [Fact]
    public void ColorSource_IsObjectOnlyWhereColourCarriesInformation()
    {
        var byObject = LayerRoleRegistry.All
            .Where(d => d.ColorFromObject)
            .Select(d => d.Role)
            .ToHashSet();

        Assert.Equal(
            new HashSet<LayerRole>
            {
                LayerRole.Terrain, LayerRole.Zones, LayerRole.Scatter,
                LayerRole.Markers, LayerRole.MarkerLabels
            },
            byObject);
    }
}
