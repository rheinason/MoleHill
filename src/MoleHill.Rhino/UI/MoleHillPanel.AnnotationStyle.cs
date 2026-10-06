using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.UI;

// Annotation style, made visible. Generated text is sized and aligned by a Rhino annotation style, but
// that style used to be named only deep in the layer template editor, and whether a card followed it at
// all had no control anywhere. The Annotations tab now says which style is in use and how big its text
// is, lets you switch it or open Rhino's editor on it, and every card says whether it follows it.
public sealed partial class MoleHillPanel
{
    /// <summary>
    /// The tab-level row under the Annotations toolbar: the style this terrain's text binds to, its
    /// effective text height and font, a picker over the document's styles, and a way into Rhino's editor.
    /// </summary>
    private Control BuildAnnotationStyleRow(TerrainDefinition terrain)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        const string help =
            "The Rhino annotation style every label, section and table on this terrain is drawn in: its " +
            "text height, font and alignment. MoleHill never stores its own text size — change it in the " +
            "style and the terrain redraws. Shared by every terrain on the same layer template.";
        if (doc == null)
            return new Panel();

        string current = LayerRoleService.ResolveAnnotationStyleName(doc, terrain);
        IReadOnlyList<string> names = AnnotationStyleService.ListStyleNames(doc);
        if (!names.Contains(current, StringComparer.OrdinalIgnoreCase))
            names = names.Append(current).ToList();

        var picker = new DropDown();
        foreach (string name in names)
            picker.Items.Add(new ListItem { Text = name, Key = name });
        picker.SelectedKey = names.First(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
        ApplyHelp(picker, help);
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (_isRefreshing || picker.SelectedKey is not { } chosen ||
                string.Equals(chosen, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            OnAnnotationStyleChosen(terrain.TerrainId, chosen);
        };

        var editButton = MakeInlineButton(
            "Edit…",
            (_, _) => RhinoApp.RunScript("_DocumentProperties", false),
            "Open Rhino's Document Properties; choose Annotation Styles and edit “" + current + "”. " +
            "The terrain redraws as soon as the style changes.");

        var readout = UiControls.Label(DescribeAnnotationStyle(doc, current), UiLabelRole.Meta, WrapMode.Word);
        ApplyHelp(readout, help);

        var row = new PropertyRow(
            CreateHelpLabel("Text Style", help, 0),
            CreateResponsivePrimaryActionRow(picker, UiMetrics.SpaceSmall, editButton),
            expandWidget: true);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceXSmall,
            Padding = new Padding(UiMetrics.SectionHorizontalPadding, 0, UiMetrics.SectionHorizontalPadding, UiMetrics.SectionBottomPadding),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(row, HorizontalAlignment.Stretch),
                new StackLayoutItem(new PropertyRow(new Panel(), readout, expandWidget: true), HorizontalAlignment.Stretch)
            }
        };
    }

    /// <summary>"2.50 m text · Arial" — what the style will actually draw, in model units.</summary>
    private static string DescribeAnnotationStyle(RhinoDoc doc, string styleName)
    {
        DimensionStyle? style = AnnotationStyleService.FindStyle(doc, styleName);
        if (style == null)
            return "Created from the document's current style on the next build.";

        double height = AnnotationStyleService.GetEffectiveTextHeight(style);
        string font = style.Font?.FamilyName ?? string.Empty;
        string size = $"{height.ToString("0.###", CultureInfo.CurrentCulture)} {ModelUnits.Abbreviation(doc.ModelUnitSystem)} text";
        return string.IsNullOrWhiteSpace(font) ? size : $"{size} · {font}";
    }

    private void OnAnnotationStyleChosen(Guid terrainId, string styleName)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        TerrainDefinition? terrain = doc == null
            ? null
            : _controller.GetTerrains(doc).FirstOrDefault(item => item.TerrainId == terrainId);
        if (doc == null || terrain == null)
            return;

        if (!LayerRoleService.SetAnnotationStyle(doc, terrain, styleName))
        {
            RhinoApp.WriteLine("MoleHill: this document has no layer template to set the style on — use Terrain Settings → Output Layers → Edit….");
            RefreshUi();
            return;
        }

        // The style is a template setting, so every terrain routed through the same template is now
        // sized by it, not just the selected one.
        string template = LayerRoleService.GetTable(doc, terrain).TemplateName;
        foreach (TerrainDefinition other in _controller.GetTerrains(doc))
        {
            if (string.Equals(LayerRoleService.GetTable(doc, other).TemplateName, template, StringComparison.OrdinalIgnoreCase))
                _controller.RebuildTerrain(doc, other.TerrainId);
        }

        RefreshUi();
    }

    /// <summary>
    /// The per-card Text Size row: which style this card's text is drawn in, and how big that is.
    ///
    /// <see cref="AnnotationDefinition.FollowsAnnotationStyle"/> had no control at all. New cards always
    /// follow the style, and that is the design: bake stamps every generated text with the style, which
    /// resets its height, so a card-specific height would preview at one size and bake at another. Only
    /// documents from before schema 27 hold fixed heights. Those cards say so, and offer the way back,
    /// rather than this row offering a new way to diverge.
    /// </summary>
    private void AppendAnnotationTextSizeRows(DynamicLayout layout, TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        string styleName = doc == null ? AnnotationStyleService.DefaultStyleName : LayerRoleService.ResolveAnnotationStyleName(doc, terrain);
        bool blocks = annotation is BlockAttributeAnnotationDefinition and not GradeBetweenPointsAnnotationDefinition;

        if (annotation.FollowsAnnotationStyle)
        {
            string readout = doc == null ? styleName : $"{styleName} — {DescribeAnnotationStyle(doc, styleName)}";
            layout.AddRow(CreateReadOnlyValueRow(
                "Text Size",
                readout,
                blocks
                    ? "Labels are sized from the annotation style chosen at the top of this tab; Block Scale " +
                      "multiplies that. Edit the style to resize every label at once."
                    : "Text is drawn in the annotation style chosen at the top of this tab. Edit the style to " +
                      "resize every label, section and table at once."));
            return;
        }

        var follow = MakeInlineButton(
            "Follow Style",
            (_, _) =>
            {
                MutateAnnotation(terrain.TerrainId, annotation.Id, item => item.FollowsAnnotationStyle = true, scheduleRebuild: true);
                RefreshUi();
            },
            $"Size this card's text from “{styleName}” like every new card.");
        var note = UiControls.Label(
            blocks ? "Fixed: Block Scale is absolute." : "Fixed height from an older document.",
            UiLabelRole.Meta,
            WrapMode.Word);
        layout.AddRow(new PropertyRow(
            CreateHelpLabel(
                "Text Size",
                "This card keeps the absolute size an older document gave it. Baking stamps text with the " +
                "annotation style, which sets its size, so preview and bake can differ until it follows the style.",
                0),
            CreateResponsivePrimaryActionRow(note, UiMetrics.SpaceSmall, follow),
            expandWidget: true));
    }
}
