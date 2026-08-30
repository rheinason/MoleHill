using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The no-escape invariant: every generated object names a destination, and every destination
/// resolves to a real layer.
///
/// Before roles, marker blocks, marker value labels, scatter instances and in-situ stair output set
/// no layer at all, and grading auxiliary output used a nullable field that was blank whenever the
/// user cleared the auxiliary layer. All of them baked onto whatever layer the user happened to be
/// working on at the time.
/// </summary>
public class GeneratedObjectRoutingTests
{
    /// <summary>
    /// The real enforcement is that <c>GeneratedRhinoObject.Role</c> is a required member, so the
    /// compiler stops anything being constructed without a destination. This pins the guarantee that
    /// makes that worth having: whatever role is named, a path comes back.
    /// </summary>
    [Fact]
    public void EveryRole_ResolvesToANonEmptyPath()
    {
        foreach (LayerRole role in Enum.GetValues<LayerRole>())
        {
            string path = LayerRoleTable.Default.Path(role);
            Assert.False(string.IsNullOrWhiteSpace(path), $"Role {role} resolved to no layer.");
            Assert.DoesNotContain("::::", path, StringComparison.Ordinal);
            Assert.False(path.EndsWith("::", StringComparison.Ordinal), $"Role {role} has a trailing separator.");
        }
    }

    /// <summary>
    /// The output that used to escape. Named individually rather than looped, so that removing one
    /// of these roles is a deliberate act with a failing test attached.
    /// </summary>
    [Theory]
    [InlineData(LayerRole.Markers)]
    [InlineData(LayerRole.MarkerLabels)]
    [InlineData(LayerRole.Scatter)]
    [InlineData(LayerRole.GradingAuxiliary)]
    public void OutputThatUsedToEscape_NowHasADestination(LayerRole role)
    {
        Assert.False(string.IsNullOrWhiteSpace(LayerRoleTable.Default.Path(role)));
    }

    /// <summary>
    /// A rebound template must not strand anything: whatever the office points its layers at, every
    /// role still resolves.
    /// </summary>
    [Fact]
    public void ARetargetedTemplate_StillRoutesEveryRole()
    {
        var template = new LayerTemplateDefinition
        {
            Version = 1,
            Name = "Office",
            Entries = new List<LayerTemplateEntry>
            {
                new() { Roles = { "annotation" }, Path = "Drawing::Site" },
                new() { Roles = { "terrain" }, Path = "Model::Proposed" },
                new() { Roles = { "auxiliary" }, Path = "Model::Structures" },
                new() { Roles = { "zones" }, Path = "Model::Areas" },
                new() { Roles = { "scatter" }, Path = "Model::Planting" }
            }
        };

        var table = LayerRoleTable.Build(template);
        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            Assert.False(string.IsNullOrWhiteSpace(table.Path(role)));

        Assert.Equal("Drawing::Site::Sections::CutFill::Cut", table.Path(LayerRole.SectionsCutFillCut));
        Assert.Equal("Model::Structures::Walls", table.Path(LayerRole.Walls));
    }

    /// <summary>
    /// Preview and bake must agree, so both have to read the same record for a given role. Asserting
    /// on reference equality rather than value equality also pins that the table is not rebuilding
    /// an equivalent record per call, which would make the two paths able to drift again.
    /// </summary>
    [Fact]
    public void PreviewAndBake_ReadTheSameAppearanceRecord()
    {
        var table = LayerRoleTable.Default;

        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            Assert.Same(table.Appearance(role), table[role].Appearance);
    }

    /// <summary>
    /// Drawing output stays ByLayer so the layer table governs the print. Output whose colour is
    /// data cannot be, and this is invisible until something is printed.
    /// </summary>
    [Fact]
    public void DrawingOutput_IsLayerColouredAndDataOutputIsNot()
    {
        var table = LayerRoleTable.Default;

        foreach (var role in new[]
                 {
                     LayerRole.ContoursMajor, LayerRole.Waterflow, LayerRole.Sections,
                     LayerRole.SectionsGrid, LayerRole.SectionsCutFillCut, LayerRole.Walls
                 })
        {
            Assert.False(table.Appearance(role).ColorFromObject, $"{role} should be ByLayer.");
        }

        foreach (var role in new[] { LayerRole.Terrain, LayerRole.Zones, LayerRole.Scatter })
            Assert.True(table.Appearance(role).ColorFromObject, $"{role} carries its colour as data.");
    }

    /// <summary>
    /// The build cache keys on the table's fingerprint, so retargeting or restyling has to move it —
    /// otherwise a template edit would leave stale geometry on the old layers.
    /// </summary>
    [Fact]
    public void ChangingTheTemplate_MovesTheFingerprint()
    {
        var baseline = LayerRoleTable.Default.Fingerprint;

        var retargeted = LayerRoleTable.Build(new LayerTemplateDefinition
        {
            Version = 1,
            Name = "Office",
            Entries = new List<LayerTemplateEntry> { new() { Roles = { "annotation" }, Path = "Drawing::Site" } }
        });

        Assert.NotEqual(baseline, retargeted.Fingerprint);
    }
}
