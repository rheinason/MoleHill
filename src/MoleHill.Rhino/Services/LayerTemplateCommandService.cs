using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

internal static class LayerTemplateCommandService
{
    public static Result RunApplyLayerTemplate(RhinoDoc doc)
    {
        if (!TryChooseTemplate(doc, "Choose layer template", out LayerTemplateDefinition? template, out Result result))
            return result;

        // Create-only. Applying a template must never restyle layers that already exist: the user's
        // Layers-panel edits and per-detail print overrides are theirs, and this command used to
        // discard them silently on every run. Re-stamping is mhResetLayerStyles, which asks first.
        var counts = LayerCreationService.ApplyTemplate(doc, LayerRoleTable.Build(template!));

        doc.Views.Redraw();
        RhinoApp.WriteLine(
            $"Applied layer template '{template!.Name}' ({counts.Created} created, {counts.Existing} already present).");
        return Result.Success;
    }

    /// <summary>
    /// Re-stamps colour, print colour and print width onto layers that already exist, discarding
    /// whatever the user changed in Rhino's Layers panel. Deliberately its own command, and
    /// deliberately noisy about what it is going to do, because the everyday rule is the opposite:
    /// the template seeds a layer once, and after that the layer wins.
    /// </summary>
    public static Result RunResetLayerStyles(RhinoDoc doc)
    {
        if (!TryChooseTemplate(
                doc,
                "Choose layer template to reset styles from",
                out LayerTemplateDefinition? template,
                out Result result))
        {
            return result;
        }

        var table = LayerRoleTable.Build(template!);
        int affected = table.AllLayers.Count(layer => doc.Layers.FindByFullPath(layer.Path, -1) >= 0);
        if (affected == 0)
        {
            RhinoApp.WriteLine($"No layers from '{template!.Name}' exist in this document yet — nothing to reset.");
            return Result.Nothing;
        }

        var confirm = new GetOption();
        confirm.SetCommandPrompt(
            $"Reset {affected} layer style(s) from '{template!.Name}'? This discards colour and print width edits made in the Layers panel");
        int noIndex = confirm.AddOption("No");
        confirm.AddOption("Yes");
        if (confirm.Get() != global::Rhino.Input.GetResult.Option)
            return confirm.CommandResult();

        if (confirm.OptionIndex() == noIndex)
            return Result.Cancel;

        var counts = LayerCreationService.ApplyTemplate(doc, table, restyleExisting: true);
        doc.Views.Redraw();
        RhinoApp.WriteLine($"Reset {counts.Existing} layer style(s) from '{template.Name}' ({counts.Created} created).");
        return Result.Success;
    }

    public static Result RunEditLayerTemplates(RhinoDoc doc)
    {
        bool saved = LayerTemplateEditorDialog.ShowDialog(doc, MoleHillRhinoPlugin.Instance.LayerTemplateStore);
        return saved ? Result.Success : Result.Cancel;
    }

    private static bool TryChooseTemplate(
        RhinoDoc doc,
        string prompt,
        out LayerTemplateDefinition? template,
        out Result result)
    {
        template = null;
        result = Result.Nothing;

        var templates = MoleHillRhinoPlugin.Instance.LayerTemplateStore.LoadTemplates().ToList();
        if (templates.Count == 0)
        {
            RhinoApp.WriteLine("No layer templates are available.");
            return false;
        }

        if (templates.Count == 1)
        {
            template = templates[0];
            return true;
        }

        var getOption = new GetOption();
        getOption.SetCommandPrompt(prompt);
        var optionMap = new Dictionary<int, LayerTemplateDefinition>();
        foreach (var item in templates)
            optionMap[getOption.AddOption(item.Name.Replace(" ", string.Empty))] = item;

        if (getOption.Get() != global::Rhino.Input.GetResult.Option)
        {
            result = getOption.CommandResult();
            return false;
        }

        if (!optionMap.TryGetValue(getOption.OptionIndex(), out template))
        {
            result = Result.Cancel;
            return false;
        }

        return true;
    }
}
