using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.UI;

// Shared Plant/Orient base inspection, correction and explicit release controls.
public sealed partial class MoleHillPanel
{
    private Control CreateObjectPlacementActions(TerrainDefinition terrain, TerrainObjectDefinition definition, string cardKind)
    {
        void WithDocument(Action<RhinoDoc> action)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null) action(doc);
        }
        var show = MakeInlineButton("Show Base", (_, _) => WithDocument(doc =>
            _controller.ShowObjectBases(doc, terrain.TerrainId, definition.Id)), "Preview bases; green arrow is up, red arrow is heading.");
        var change = MakeInlineButton("Change Base", (_, _) => WithDocument(doc =>
            _controller.ChangeObjectBase(doc, terrain.TerrainId, definition.Id)), "Choose an object's box face or define a base with three points. Enter accepts; Esc cancels.");
        var releaseSelected = MakeInlineButton("Release Selected", (_, _) => WithDocument(doc =>
            _controller.ReleaseObjects(doc, terrain.TerrainId, definition.Id, selectedOnly: true)), "Keep selected objects placed and stop following this card, including watched-layer matches.");
        var releaseAll = MakeInlineButton("Release All", (_, _) => WithDocument(doc =>
            _controller.ReleaseObjects(doc, terrain.TerrainId, definition.Id, selectedOnly: false)), "Keep all matched objects placed and release them from this card.");
        // Design review Step 62: the base tools are occasional, so they sit in a closed Base section;
        // Release is the everyday action and stays on the card.
        var baseSection = new CardSection(
            cardKind,
            "base",
            "Base",
            expandedByDefault: false,
            summary: null,
            content: new AdaptiveControlGroup(UiMetrics.SpaceSmall, show, change));
        return new StackLayout
        {
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { new AdaptiveControlGroup(UiMetrics.SpaceSmall, releaseSelected, releaseAll), baseSection }
        };
    }
}
