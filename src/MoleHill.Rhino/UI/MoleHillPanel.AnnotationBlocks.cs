using Eto.Forms;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

// The block row on an annotation card. Annotation labels are block instances, and the block was always
// swappable — BlockDefinitionName is on the definition — but nothing on the card said so. This row makes
// the block a visible, editable thing: pick any block in the document, copy the built-in one into a block
// of your own, edit it in Rhino's block editor, and see at a glance whether it fills the fields MoleHill
// writes. The built-in MoleHill_* blocks are rewritten from code on every bake, so they are never edited
// in place: "Edit" on the built-in block copies it first.
public sealed partial class MoleHillPanel
{
    private Control CreateAnnotationBlockEditor(
        string label,
        string? blockName,
        MarkerBlockTemplate template,
        string? help,
        Action<string?> setBlockName)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        bool usesBuiltIn = string.IsNullOrWhiteSpace(blockName);
        string effectiveName = usesBuiltIn
            ? GeneratedBlockCatalog.GetDefaultDefinitionName(template)
            : blockName!;
        InstanceDefinition? definition = doc?.InstanceDefinitions.Find(effectiveName);
        string rowHelp = (help ?? string.Empty) + System.Environment.NewLine + System.Environment.NewLine +
                         AnnotationBlockLibrary.FieldHelp();

        var nameLabel = UiControls.Label(
            usesBuiltIn ? "Built-in" : blockName!,
            UiLabelRole.Input,
            WrapMode.None);
        ApplyHelp(nameLabel, rowHelp);

        var pick = MakeInlineButton(
            "Pick…",
            (_, _) => ChooseAnnotationBlock(blockName, setBlockName),
            "Choose any block definition in this document.");

        var newBlock = MakeInlineButton(
            "New…",
            (_, _) => CreateAnnotationBlockFromDefault(template, setBlockName),
            "Copy MoleHill's built-in block into a new block of your own and use it. Edit the copy " +
            "freely; MoleHill never rewrites it.");

        var edit = MakeInlineButton(
            "Edit",
            (_, _) => EditAnnotationBlock(blockName, template, setBlockName),
            usesBuiltIn
                ? "The built-in block is rewritten on every bake, so this first copies it into a block of " +
                  "your own, then opens that in Rhino's block editor."
                : "Open this block in Rhino's block editor. Every label using it updates.");

        var actions = new List<Control> { pick, newBlock, edit };
        if (!usesBuiltIn)
        {
            actions.Add(MakeInlineButton(
                "Built-in",
                (_, _) =>
                {
                    setBlockName(null);
                    RefreshUi();
                },
                "Go back to MoleHill's built-in block."));
        }

        AnnotationBlockFieldReport report = AnnotationBlockLibrary.Inspect(definition);
        bool problem = !usesBuiltIn && definition != null && !report.ShowsValue;
        var status = UiControls.Label(
            usesBuiltIn
                ? AnnotationBlockLibrary.DescribeBuiltIn()
                : AnnotationBlockLibrary.Describe(report, definition != null, blockName),
            problem ? UiLabelRole.Warning : UiLabelRole.Meta,
            WrapMode.Word);
        ApplyHelp(status, rowHelp);

        var row = new PropertyRow(
            CreateHelpLabel(label, rowHelp, 0),
            CreateResponsivePrimaryActionRow(nameLabel, UiMetrics.SpaceSmall, actions.ToArray()),
            expandWidget: true);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceXSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(row, HorizontalAlignment.Stretch),
                new StackLayoutItem(new PropertyRow(new Panel(), status, expandWidget: true), HorizontalAlignment.Stretch)
            }
        };
    }

    /// <summary>Every named block in the document, A–Z, each name once.</summary>
    private static List<InstanceDefinition> CollectBlockDefinitions(RhinoDoc doc, bool includeBuiltIn = true)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new List<InstanceDefinition>();
        foreach (InstanceDefinition definition in doc.InstanceDefinitions)
        {
            if (definition == null || definition.IsDeleted || string.IsNullOrWhiteSpace(definition.Name))
                continue;
            if (!includeBuiltIn && AnnotationBlockLibrary.IsBuiltInName(definition.Name))
                continue;
            if (!seen.Add(definition.Name))
                continue;

            blocks.Add(definition);
        }

        blocks.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return blocks;
    }

    private void ChooseAnnotationBlock(string? currentName, Action<string?> setBlockName)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        Application.Instance.AsyncInvoke(() =>
        {
            if (IsDisposed)
                return;

            // The built-in blocks have their own button; listing them here would only be a second way
            // to the same place, and one that goes stale if a user renames the card's block later.
            List<string>? chosen = BlockSelectorDialog.Show(
                doc,
                CollectBlockDefinitions(doc, includeBuiltIn: false),
                BlockSelectorDialog.Options.Annotation(currentName));
            if (chosen == null || chosen.Count == 0)
                return;

            setBlockName(chosen[0]);
            RefreshUi();
        });
    }

    private void CreateAnnotationBlockFromDefault(MarkerBlockTemplate template, Action<string?> setBlockName)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        Application.Instance.AsyncInvoke(() =>
        {
            if (IsDisposed)
                return;

            string suggestion = AnnotationBlockLibrary.UniqueName(doc, AnnotationBlockLibrary.DescribeTemplate(template));
            if (!Dialogs.ShowEditBox("New Annotation Block", "Name for the new block:", suggestion, false, out string entered))
                return;

            string? name = CopyDefaultBlock(doc, template, entered);
            if (name == null)
                return;

            setBlockName(name);
            RefreshUi();
        });
    }

    /// <summary>
    /// Adds a copy of the built-in block under <paramref name="requestedName"/> (made unique) and says so
    /// on the command line. Null when Rhino would not add it.
    /// </summary>
    private static string? CopyDefaultBlock(RhinoDoc doc, MarkerBlockTemplate template, string requestedName)
    {
        string name = string.IsNullOrWhiteSpace(requestedName)
            ? AnnotationBlockLibrary.DescribeTemplate(template)
            : requestedName.Trim();

        InstanceDefinition? created = AnnotationBlockLibrary.CreateFromDefault(doc, template, name);
        if (created == null)
        {
            RhinoApp.WriteLine("MoleHill: could not create the annotation block.");
            return null;
        }

        RhinoApp.WriteLine($"MoleHill: created block \"{created.Name}\". Edit it with the Edit button; it is never rewritten.");
        return created.Name;
    }

    private void EditAnnotationBlock(string? blockName, MarkerBlockTemplate template, Action<string?> setBlockName)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        Application.Instance.AsyncInvoke(() =>
        {
            if (IsDisposed)
                return;

            string? name = blockName;
            if (string.IsNullOrWhiteSpace(name) || AnnotationBlockLibrary.IsBuiltInName(name))
            {
                // Edits to a MoleHill_* block are overwritten on the next bake, so give the user a copy.
                name = CopyDefaultBlock(doc, template, AnnotationBlockLibrary.DescribeTemplate(template));
                if (name == null)
                    return;

                setBlockName(name);
                RefreshUi();
            }

            if (!AnnotationBlockEditor.TryOpen(doc, name!, out string? failure))
                RhinoApp.WriteLine($"MoleHill: {failure}");
        });
    }
}
