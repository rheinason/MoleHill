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
            return TerrainSerializer.Deserialize(json, unitContext);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            failureMessage = ex.Message;
            return null;
        }
    }

    public void SaveJson(RhinoDoc doc, string json)
    {
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
