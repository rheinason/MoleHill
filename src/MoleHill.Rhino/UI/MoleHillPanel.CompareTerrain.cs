using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

// Comparing against another terrain. Cut/Fill, Earthworks and the three section cards all let a terrain
// be compared against another one, and they share one dropdown and one status row so the choice reads the
// same everywhere. The status row exists because the comparison used to fail silently: a terrain that was
// never built, was empty, or had been edited with Live Update off gave plausible figures against the wrong
// ground. It says which case applies, and its button runs the rebuild from here, so nobody has to switch
// the active terrain to do it.
public sealed partial class MoleHillPanel
{
    private const string CompareTerrainHelp =
        "Another terrain's finished surface to compare against, without baking it to Rhino geometry first. " +
        "None compares against this terrain's own initial triangulation — the ground before any modifier moved it.";

    /// <summary>The "Compare To Terrain" dropdown: None (this terrain's base triangulation) or another terrain.</summary>
    private Control CreateCompareTerrainEditor(
        TerrainDefinition owner,
        Guid? selectedId,
        Action<Guid?> onChanged,
        string help = CompareTerrainHelp)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);

        var options = new List<(string Key, string Label)> { ("", "None — this terrain's base triangulation") };
        options.AddRange(terrains
            .Where(item => item.TerrainId != owner.TerrainId)
            .Select(item => (item.TerrainId.ToString(), item.Name)));
        if (selectedId is { } missing && options.All(option => option.Key != missing.ToString()))
            options.Add((missing.ToString(), "(missing terrain)"));

        return CreateDropDownEditor(
            "Compare To Terrain",
            options,
            selectedId?.ToString() ?? "",
            value => onChanged(string.IsNullOrEmpty(value) ? null : Guid.Parse(value)),
            help);
    }

    /// <summary>
    /// Says when the chosen terrain cannot be compared against as it stands, and offers the rebuild that
    /// fixes it. Null when the comparison is current, so a healthy card stays quiet.
    /// </summary>
    /// <param name="meanwhile">What the card shows until it is fixed, finishing "Until then, …".</param>
    private Control? CreateCompareTerrainStatusRow(TerrainDefinition owner, Guid referenceId, string meanwhile)
    {
        if (RhinoDoc.ActiveDoc is not { } doc)
            return null;

        ReferenceTerrainStatus status = _controller.GetReferenceTerrainStatus(doc, owner.TerrainId, referenceId);
        string name = $"“{status.Name}”";
        string rebuildReference = $"Rebuild {status.Name}";
        (string Message, string? Action, bool RebuildOwner) row = status.State switch
        {
            ReferenceTerrainState.Ready or ReferenceTerrainState.OwnerUpdating => (string.Empty, null, false),
            ReferenceTerrainState.Missing => (
                "The terrain chosen to compare against no longer exists. Choose another.", null, false),
            ReferenceTerrainState.Building => (
                $"{name} is building. This card updates when it finishes.", null, false),
            ReferenceTerrainState.NotBuilt => (
                $"{name} hasn't been built yet, so there is nothing to compare against. Until then, {meanwhile}.",
                rebuildReference, false),
            ReferenceTerrainState.Empty => (
                $"{name} was built but has no surface — check that its sources are set. Until then, {meanwhile}.",
                rebuildReference, false),
            ReferenceTerrainState.HasUnbuiltEdits => (
                $"{name} has edits that haven't been built (its Live Update is off), so this compares against " +
                "its previous surface.",
                rebuildReference, false),
            ReferenceTerrainState.OwnerOutOfDate => (
                $"{name} has changed since this terrain was last built, so these results compare against its " +
                "older surface.",
                "Rebuild this terrain", true),
            _ => (string.Empty, null, false)
        };

        if (row.Message.Length == 0)
            return null;

        if (status.State == ReferenceTerrainState.Building)
            return CreateNoteRow(row.Message);

        var content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Items =
            {
                new StackLayoutItem(new Label
                {
                    Text = row.Message,
                    TextColor = UiTheme.WarningText,
                    Wrap = WrapMode.Word
                }, HorizontalAlignment.Stretch)
            }
        };

        if (row.Action is { } action)
        {
            Guid ownerId = owner.TerrainId;
            bool rebuildOwner = row.RebuildOwner;
            content.Items.Add(MakeInlineButton(
                action,
                (_, _) =>
                {
                    if (RhinoDoc.ActiveDoc is not { } activeDoc)
                        return;

                    if (rebuildOwner)
                        _controller.RebuildTerrain(activeDoc, ownerId);
                    else
                        _controller.RebuildReferenceTerrain(activeDoc, ownerId, referenceId);
                    RefreshUi();
                },
                rebuildOwner
                    ? "Rebuild this terrain against the current surface of the terrain it compares to."
                    : $"Rebuild {status.Name} without switching to it. This terrain rebuilds after it, so the " +
                      "comparison updates whatever either one's Live Update setting."));
        }

        return new Panel
        {
            BackgroundColor = UiTheme.WarningBackground,
            Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall),
            Content = content
        };
    }
}
