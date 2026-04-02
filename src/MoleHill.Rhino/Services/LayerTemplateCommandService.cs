using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

internal static class LayerTemplateCommandService
{
    public static Result RunApplyLayerTemplate(RhinoDoc doc)
    {
        var store = MoleHillRhinoPlugin.Instance.LayerTemplateStore;
        var templates = store.LoadTemplates().ToList();
        if (templates.Count == 0)
        {
            RhinoApp.WriteLine("No layer templates are available.");
            return Result.Nothing;
        }

        LayerTemplateDefinition template = templates[0];
        if (templates.Count > 1)
        {
            var getOption = new GetOption();
            getOption.SetCommandPrompt("Choose layer template");
            var optionMap = new Dictionary<int, LayerTemplateDefinition>();
            foreach (var item in templates)
                optionMap[getOption.AddOption(item.Name.Replace(" ", string.Empty))] = item;

            if (getOption.Get() != global::Rhino.Input.GetResult.Option)
                return getOption.CommandResult();

            if (!optionMap.TryGetValue(getOption.OptionIndex(), out template!))
                return Result.Cancel;
        }

        foreach (var entry in template.Entries)
            EnsureLayer(doc, entry);

        doc.Views.Redraw();
        RhinoApp.WriteLine($"Applied layer template '{template.Name}'.");
        return Result.Success;
    }

    public static Result RunEditLayerTemplates(RhinoDoc doc)
    {
        bool saved = LayerTemplateEditorDialog.ShowDialog(doc, MoleHillRhinoPlugin.Instance.LayerTemplateStore);
        return saved ? Result.Success : Result.Cancel;
    }

    private static void EnsureLayer(RhinoDoc doc, LayerTemplateEntry entry)
    {
        string[] segments = entry.Path
            .Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int parentIndex = -1;
        string currentPath = string.Empty;
        for (int i = 0; i < segments.Length; i++)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? segments[i] : $"{currentPath}::{segments[i]}";
            int layerIndex = doc.Layers.FindByFullPath(currentPath, -1);
            if (layerIndex < 0)
            {
                var layer = new Layer
                {
                    Name = segments[i],
                    Color = System.Drawing.Color.FromArgb(entry.ColorArgb),
                    PlotColor = System.Drawing.Color.FromArgb(entry.PrintColorArgb),
                    PlotWeight = entry.PlotWeight
                };
                if (parentIndex >= 0)
                    layer.ParentLayerId = doc.Layers[parentIndex].Id;

                layerIndex = doc.Layers.Add(layer);
            }
            else
            {
                Layer updated = doc.Layers[layerIndex];
                updated.Color = System.Drawing.Color.FromArgb(entry.ColorArgb);
                updated.PlotColor = System.Drawing.Color.FromArgb(entry.PrintColorArgb);
                updated.PlotWeight = entry.PlotWeight;
                doc.Layers.Modify(updated, layerIndex, quiet: true);
            }

            parentIndex = layerIndex;
        }
    }
}
