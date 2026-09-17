using System.Text.Json;
using Rhino;
using MoleHill.Rhino.Model;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainDocumentStore
{
    private const string Section = "MoleHill.Rhino";
    private const string Entry = "Terrains";
    private const string BackupEntry = "Terrains.backup";

    public string? LoadJson(RhinoDoc doc) => doc.Strings.GetValue(Section, Entry);

    /// <summary>
    /// Loads and deserializes the stored terrain JSON. Returns null (rather than throwing) when the
    /// JSON is truncated or carries a `$type` discriminator this build's registry doesn't know —
    /// e.g. a document saved by a newer plugin version. Callers must treat null as "unreadable",
    /// distinct from "no terrains" (an empty list).
    /// </summary>
    public List<TerrainDefinition>? Load(RhinoDoc doc, out string? failureMessage)
    {
        failureMessage = null;
        string? json = LoadJson(doc);
        try
        {
            ModelUnitContext unitContext = ModelUnitContext.FromDocument(doc);
            if (!unitContext.IsSupported)
                unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);

            List<TerrainDefinition> terrains = TerrainSerializer.Deserialize(json, unitContext);
            MigrateLayerRouting(doc, terrains);
            return terrains;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            failureMessage = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Carries a pre-schema-30 document's customised output layers into a template of its own, so
    /// its geometry keeps landing where it landed. Detected from the legacy values themselves rather
    /// than from a version number, since they are only ever populated by an older document and are
    /// cleared once carried over.
    /// </summary>
    private static void MigrateLayerRouting(RhinoDoc doc, List<TerrainDefinition> terrains)
    {
        string documentName = string.IsNullOrWhiteSpace(doc.Name) ? "Document" : Path.GetFileNameWithoutExtension(doc.Name);
        LayerRoutingMigration.Result result = LayerRoutingMigration.Plan(terrains, documentName);
        if (!result.HasTemplate)
        {
            LayerRoutingMigration.ClearLegacyValues(terrains);
            return;
        }

        LayerTemplateDocumentStore.Embed(
            doc,
            result.Template!,
            LayerRoleTable.Build(result.Template!).Fingerprint,
            makeActive: true);
        LayerRoleService.Invalidate(doc);

        foreach (TerrainDefinition terrain in terrains)
            terrain.LayerTemplateName = result.Template!.Name;

        RhinoApp.WriteLine(
            $"MoleHill: this document's output layers moved into a layer template, '{result.Template!.Name}'. "
                + "Nothing has moved in the drawing; the routing is now editable in one place.");

        foreach (string conflict in result.Conflicts)
            RhinoApp.WriteLine("MoleHill: " + conflict);

        LayerRoutingMigration.ClearLegacyValues(terrains);
    }

    public void SaveJson(RhinoDoc doc, string json)
    {
        TerrainDocumentIdentity.Ensure(doc);
        doc.Strings.SetString(Section, Entry, json);
    }

    public void Save(RhinoDoc doc, IReadOnlyList<TerrainDefinition> terrains)
    {
        string? previousJson = LoadJson(doc);
        if (!string.IsNullOrWhiteSpace(previousJson))
            doc.Strings.SetString(Section, BackupEntry, previousJson);

        string json = TerrainSerializer.Serialize(terrains);
        SaveJson(doc, json);
    }
}
