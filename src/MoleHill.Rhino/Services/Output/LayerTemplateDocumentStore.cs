using System.Text.Json;
using System.Text.Json.Serialization;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Reads and writes the layer templates a document carries, in the same plugin document strings as
/// the terrain state.
/// </summary>
internal static class LayerTemplateDocumentStore
{
    private const string Section = "MoleHill.Rhino";
    private const string Entry = "LayerTemplates";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The document's own templates, or null when it has never been given any.</summary>
    public static EmbeddedLayerTemplateState? Load(RhinoDoc doc)
    {
        string? json = doc.Strings.GetValue(Section, Entry);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<EmbeddedLayerTemplateState>(json, JsonOptions);
            if (state?.Templates == null || state.Templates.Count == 0)
                return null;

            foreach (var embedded in state.Templates)
                LayerTemplateStore.NormalizeTemplates(new List<LayerTemplateDefinition> { embedded.Template });

            return state;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A document written by a newer build, or a truncated string. Falling back to the local
            // template is better than refusing to draw; the document keeps its copy either way,
            // since nothing overwrites it unless the user asks.
            return null;
        }
    }

    public static void Save(RhinoDoc doc, EmbeddedLayerTemplateState state)
    {
        doc.Strings.SetString(Section, Entry, JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>
    /// Copies a template into the document, replacing any copy of the same name. Recording the local
    /// fingerprint at the same time is what lets divergence be detected later without a diff.
    /// </summary>
    public static void Embed(
        RhinoDoc doc,
        LayerTemplateDefinition template,
        ulong localFingerprint,
        bool makeActive)
    {
        EmbeddedLayerTemplateState state = Load(doc) ?? new EmbeddedLayerTemplateState();

        state.Templates.RemoveAll(item =>
            string.Equals(item.Template.Name, template.Name, StringComparison.OrdinalIgnoreCase));

        state.Templates.Add(new EmbeddedLayerTemplate
        {
            Template = template,
            LocalFingerprintWhenEmbedded = localFingerprint,
            CapturedUtc = DateTimeOffset.UtcNow
        });

        if (makeActive)
            state.ActiveName = template.Name;

        Save(doc, state);
    }

    public static LayerTemplateDefinition? Find(EmbeddedLayerTemplateState? state, string? name)
    {
        if (state == null)
            return null;

        string wanted = string.IsNullOrWhiteSpace(name) ? state.ActiveName : name!;
        EmbeddedLayerTemplate? match = state.Templates.FirstOrDefault(item =>
            string.Equals(item.Template.Name, wanted, StringComparison.OrdinalIgnoreCase));

        // A terrain naming a template the document does not carry falls back to the active one
        // rather than losing its routing.
        match ??= state.Templates.FirstOrDefault(item =>
            string.Equals(item.Template.Name, state.ActiveName, StringComparison.OrdinalIgnoreCase));

        return (match ?? state.Templates.FirstOrDefault())?.Template;
    }
}
