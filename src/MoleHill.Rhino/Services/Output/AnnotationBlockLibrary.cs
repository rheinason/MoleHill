using System.Text;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime;

namespace MoleHill.Rhino.Services;

/// <summary>What a block definition offers an annotation card: which MoleHill fields it declares.</summary>
internal readonly record struct AnnotationBlockFieldReport(
    IReadOnlyList<string> Recognised,
    IReadOnlyList<string> Unrecognised,
    bool ShowsValue)
{
    public static readonly AnnotationBlockFieldReport None = new(
        Array.Empty<string>(), Array.Empty<string>(), false);
}

/// <summary>
/// The user-facing side of annotation blocks: copying a built-in block into an editable one of the
/// user's own, naming it, and reporting which fields a block declares. The built-in <c>MoleHill_*</c>
/// definitions are rewritten from code on every bake, so a user's edits to one would be lost; a copy under
/// another name is never touched, and picking it on a card is all it takes to use it.
/// </summary>
internal static class AnnotationBlockLibrary
{
    /// <summary>The fields a block's text can show. Anything else a block declares keeps its default.</summary>
    public static IReadOnlyList<string> KnownFieldKeys => BlockAttributePayload.KnownPayloadKeys;

    /// <summary>
    /// The text fields that print the label's value. A block with neither draws its symbol and no number,
    /// which is the one mistake worth warning about.
    /// </summary>
    private static readonly string[] ValueFieldKeys =
    {
        GeneratedBlockCatalog.ValueToken,
        GeneratedBlockCatalog.DisplayToken
    };

    public static bool IsBuiltInName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (MarkerBlockTemplate template in new[]
                 {
                     MarkerBlockTemplate.AnnotationElevation,
                     MarkerBlockTemplate.AnnotationSlope,
                     MarkerBlockTemplate.Elevation,
                     MarkerBlockTemplate.Slope
                 })
        {
            if (string.Equals(name, GeneratedBlockCatalog.GetDefaultDefinitionName(template), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>The name offered for a copy of <paramref name="template"/>'s built-in block.</summary>
    public static string DescribeTemplate(MarkerBlockTemplate template) => template switch
    {
        MarkerBlockTemplate.AnnotationElevation => "Elevation Label",
        MarkerBlockTemplate.AnnotationSlope => "Slope Label",
        _ => "Annotation Block"
    };

    /// <summary>
    /// <paramref name="preferred"/> if no definition carries it, otherwise the first free "name 2", "name 3".
    /// Rhino compares block names case-insensitively, so this does too.
    /// </summary>
    public static string UniqueName(RhinoDoc doc, string preferred)
    {
        string baseName = string.IsNullOrWhiteSpace(preferred) ? "Annotation Block" : preferred.Trim();
        if (doc.InstanceDefinitions.Find(baseName) == null && !IsBuiltInName(baseName))
            return baseName;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (doc.InstanceDefinitions.Find(candidate) == null && !IsBuiltInName(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Adds a block definition holding a copy of <paramref name="template"/>'s built-in artwork — symbol,
    /// text and fields — under <paramref name="name"/> (made unique). Returns the new definition, or null
    /// when the template has no artwork or Rhino refuses the add.
    /// </summary>
    public static InstanceDefinition? CreateFromDefault(RhinoDoc doc, MarkerBlockTemplate template, string name)
    {
        List<GeometryBase> geometry = GeneratedBlockCatalog.CreateBlockGeometry(template);
        if (geometry.Count == 0)
            return null;

        string finalName = UniqueName(doc, name);
        List<ObjectAttributes> attributes = geometry.Select(_ => CreateMemberAttributes()).ToList();
        int index = doc.InstanceDefinitions.Add(finalName, string.Empty, Point3d.Origin, geometry, attributes);
        return index < 0 ? null : doc.InstanceDefinitions[index];
    }

    /// <summary>
    /// Block members take colour, material, linetype and plot colour from the instance, which is what lets
    /// one block be drawn in whatever colour the card or layer asks for.
    /// </summary>
    public static ObjectAttributes CreateMemberAttributes() => new()
    {
        ColorSource = ObjectColorSource.ColorFromParent,
        PlotColorSource = ObjectPlotColorSource.PlotColorFromParent,
        MaterialSource = ObjectMaterialSource.MaterialFromParent,
        LinetypeSource = ObjectLinetypeSource.LinetypeFromParent
    };

    /// <summary>Sorts the keys a block declares into the ones MoleHill fills and the ones it does not.</summary>
    public static AnnotationBlockFieldReport Classify(IEnumerable<string> declaredKeys)
    {
        var recognised = new List<string>();
        var unrecognised = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string key in declaredKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            string? known = KnownFieldKeys.FirstOrDefault(
                candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
            if (known != null)
                recognised.Add(known);
            else
                unrecognised.Add(key);
        }

        bool showsValue = recognised.Any(
            key => ValueFieldKeys.Contains(key, StringComparer.OrdinalIgnoreCase));
        return new AnnotationBlockFieldReport(recognised, unrecognised, showsValue);
    }

    /// <summary>The fields <paramref name="definition"/> declares, read from its text.</summary>
    public static AnnotationBlockFieldReport Inspect(InstanceDefinition? definition)
    {
        if (definition == null)
            return AnnotationBlockFieldReport.None;

        return Classify(TextFields.GetInstanceAttributeFields(definition).Select(field => field.Key));
    }

    /// <summary>One line for the card: what the block fills, or what is wrong with it.</summary>
    public static string Describe(AnnotationBlockFieldReport report, bool hasDefinition, string? blockName = null)
    {
        // A missing custom block previews as the built-in symbol, and baking creates a block under the
        // card's name from the built-in artwork (TerrainController.EnsureBlockDefinition).
        if (!hasDefinition)
        {
            string named = string.IsNullOrWhiteSpace(blockName) ? "with this name" : $"named “{blockName}”";
            return $"No block {named} in this document. The built-in symbol is drawn, and baking creates the block from it.";
        }

        if (report.Recognised.Count == 0)
        {
            return report.Unrecognised.Count == 0
                ? "No MoleHill fields in this block, so it draws no value. Add a text with a field such as " +
                  FieldFormula(GeneratedBlockCatalog.ValueToken) + "."
                : "None of this block's fields are ones MoleHill fills (" + string.Join(", ", report.Unrecognised) +
                  "). Use one of: " + string.Join(", ", KnownFieldKeys) + ".";
        }

        string line = "Fills: " + string.Join(", ", report.Recognised);
        if (!report.ShowsValue)
            line += " — but no VALUE or Display field, so no number is drawn";
        if (report.Unrecognised.Count > 0)
            line += ". Left at default: " + string.Join(", ", report.Unrecognised);
        return line;
    }

    private static readonly Regex FieldPattern = new(
        @"%<UserText\(\s*""(?<source>[^""]*)""\s*,\s*""(?<key>[^""]*)""" +
        @"(?:\s*,\s*""(?<prompt>[^""]*)"")?(?:\s*,\s*""(?<default>[^""]*)"")?\s*\)>%",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Fills each <c>%&lt;UserText("block","KEY",…)&gt;%</c> field in a block's text with the label's value
    /// for KEY (case-insensitive), falling back to the field's own default. This is what the preview shows
    /// where the baked block would show its attribute text. Text with no such field is returned untouched,
    /// so a block can carry fixed text — a unit, a note — beside its fields. Returns false when
    /// <paramref name="richText"/> holds no field.
    /// </summary>
    public static bool TryResolveFields(
        string richText,
        IReadOnlyDictionary<string, string>? values,
        out string resolved)
    {
        resolved = richText;
        if (string.IsNullOrEmpty(richText) || richText.IndexOf("UserText", StringComparison.Ordinal) < 0)
            return false;

        bool isRtf = richText.StartsWith(@"{\rtf", StringComparison.Ordinal);
        bool any = false;
        string result = FieldPattern.Replace(richText, match =>
        {
            any = true;
            string key = match.Groups["key"].Value;
            string value = match.Groups["default"].Success ? match.Groups["default"].Value : string.Empty;
            if (values != null)
            {
                foreach (var pair in values)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value ?? string.Empty;
                        break;
                    }
                }
            }

            return isRtf ? EscapeRtf(value) : value;
        });

        if (!any)
            return false;

        resolved = result;
        return true;
    }

    private static string EscapeRtf(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '\\' or '{' or '}')
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The card line for the built-in block, which is not in the document until the first bake. It is
    /// rewritten on every bake, so the line says how to get an editable one.
    /// </summary>
    public static string DescribeBuiltIn() =>
        "MoleHill's built-in symbol (fills Prefix, VALUE, Suffix). Use New… or Edit to make one of your own.";

    /// <summary>The field text to put in a block's text object for <paramref name="key"/>.</summary>
    public static string FieldFormula(string key) => $@"%<UserText(""block"",""{key}"","""","""")>%";

    /// <summary>Every field MoleHill fills, with its formula, for the card's help text.</summary>
    public static string FieldHelp()
    {
        var lines = new List<string>
        {
            "Edit the block, add a text object, and type one of these fields into it:"
        };
        foreach (string key in KnownFieldKeys)
            lines.Add($"  {key}  →  {FieldFormula(key)}");
        lines.Add("Display is prefix + value + suffix in one; VALUE is the number alone.");
        return string.Join(System.Environment.NewLine, lines);
    }
}
