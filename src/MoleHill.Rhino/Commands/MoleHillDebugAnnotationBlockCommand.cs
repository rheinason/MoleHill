using System.Collections.Specialized;
using System.Text;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Runtime;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillDebugAnnotationBlockCommand : Command
{
    private static readonly string[] ExpectedKeys =
    {
        GeneratedBlockCatalog.DisplayToken,
        GeneratedBlockCatalog.ValueToken,
        GeneratedBlockCatalog.PrefixToken,
        GeneratedBlockCatalog.SuffixToken,
        GeneratedBlockCatalog.UnitToken,
        GeneratedBlockCatalog.NameToken,
        GeneratedBlockCatalog.IndexToken,
        GeneratedBlockCatalog.DistanceToken
    };

    public override string EnglishName => "MoleHillDebugAnnotationBlock";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select a baked MoleHill annotation block");
        getObject.GeometryFilter = ObjectType.InstanceReference;
        getObject.SubObjectSelect = false;
        getObject.DeselectAllBeforePostSelect = false;
        getObject.EnablePreSelect(true, true);

        if (getObject.Get() != GetResult.Object)
            return getObject.CommandResult();

        var objRef = getObject.Object(0);
        if (objRef?.Object() is not InstanceObject instanceObject)
        {
            RhinoApp.WriteLine("Selected object is not a block instance.");
            return Result.Nothing;
        }

        string report = BuildReport(instanceObject);
        RhinoApp.WriteLine(report);
        return Result.Success;
    }

    private static string BuildReport(InstanceObject instanceObject)
    {
        var sb = new StringBuilder();
        AppendLine(sb, "MoleHill annotation block debug");
        AppendLine(sb, new string('-', 48));
        AppendLine(sb, $"Object Id: {instanceObject.Id}");
        AppendLine(sb, $"Object Name: {instanceObject.Name}");
        AppendLine(sb, $"Definition: {instanceObject.InstanceDefinition?.Name ?? "<null>"}");
        AppendLine(sb, $"Definition Id: {instanceObject.InstanceDefinition?.Id.ToString() ?? "<null>"}");
        AppendLine(sb, $"Instance Xform: {instanceObject.InstanceXform}");
        AppendLine(sb, string.Empty);

        var attributeUserStrings = instanceObject.Attributes.GetUserStrings();
        AppendExpectedKeys(sb, "Object attribute user text", attributeUserStrings);
        AppendAllUserStrings(sb, "Object attribute user text (all)", attributeUserStrings);
        AppendLine(sb, string.Empty);

        if (instanceObject.Geometry is InstanceReferenceGeometry instanceGeometry)
        {
            var geometryUserStrings = instanceGeometry.GetUserStrings();
            AppendExpectedKeys(sb, "Instance geometry user text", geometryUserStrings);
            AppendAllUserStrings(sb, "Instance geometry user text (all)", geometryUserStrings);
        }
        else
        {
            AppendLine(sb, $"Geometry type: {instanceObject.Geometry?.GetType().FullName ?? "<null>"}");
        }

        AppendLine(sb, string.Empty);
        AppendDefinitionTextObjects(sb, instanceObject.InstanceDefinition);
        return sb.ToString();
    }

    private static void AppendDefinitionTextObjects(StringBuilder sb, InstanceDefinition? definition)
    {
        if (definition == null)
        {
            AppendLine(sb, "Instance definition is null.");
            return;
        }

        AppendLine(sb, $"Definition text objects: {definition.GetObjects().Count(obj => obj is TextObject)}");
        AppendLine(sb, $"Definition attribute fields: {FormatAttributeFields(TextFields.GetInstanceAttributeFields(definition))}");

        int textIndex = 0;
        foreach (var child in definition.GetObjects())
        {
            if (child is not TextObject textObject)
                continue;

            textIndex++;
            AppendLine(sb, $"Text object {textIndex}:");
            AppendLine(sb, $"  Id: {textObject.Id}");
            AppendLine(sb, $"  DisplayText: {ValueOrMarker(textObject.DisplayText)}");
            AppendLine(sb, $"  PlainText: {ValueOrMarker(textObject.TextGeometry.PlainText)}");
            AppendLine(sb, $"  TextFormula: {ValueOrMarker(textObject.TextGeometry.TextFormula)}");
            AppendLine(sb, $"  Fields: {FormatAttributeFields(TextFields.GetInstanceAttributeFields(textObject))}");
        }
    }

    private static void AppendExpectedKeys(StringBuilder sb, string title, NameValueCollection? strings)
    {
        AppendLine(sb, title);
        foreach (string key in ExpectedKeys)
        {
            string? value = strings?[key];
            string state = value == null
                ? "<missing>"
                : value.Length == 0
                    ? "<empty>"
                    : value;
            AppendLine(sb, $"  {key}: {state}");
        }
    }

    private static void AppendAllUserStrings(StringBuilder sb, string title, NameValueCollection? strings)
    {
        AppendLine(sb, title);
        if (strings == null || strings.Count == 0)
        {
            AppendLine(sb, "  <none>");
            return;
        }

        foreach (string? key in strings.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            AppendLine(sb, $"  {key}: {ValueOrMarker(strings[key])}");
        }
    }

    private static string FormatAttributeFields(TextFields.InstanceAttributeField[] fields)
    {
        if (fields.Length == 0)
            return "<none>";

        return string.Join(", ", fields.Select(field =>
            $"{field.Key} (prompt={ValueOrMarker(field.Prompt)}, default={ValueOrMarker(field.DefaultValue)})"));
    }

    private static string ValueOrMarker(string? value)
    {
        return value == null
            ? "<null>"
            : value.Length == 0
                ? "<empty>"
                : value;
    }

    private static void AppendLine(StringBuilder sb, string line)
    {
        sb.AppendLine(line);
    }
}
