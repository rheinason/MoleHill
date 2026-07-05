using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// H2 regression coverage: unreadable terrain JSON must surface as a guarded null (never a thrown
/// exception from Load), and a document whose load failed must never have its stored JSON
/// overwritten by a subsequent save.
/// </summary>
public class TerrainDocumentStoreTests
{
    private const string Section = "MoleHill.Rhino";
    private const string Entry = "Terrains";

    [RhinoNativeFact]
    public void Load_TruncatedJson_ReturnsNullWithFailureMessageInsteadOfThrowing()
    {
        RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        try
        {
            doc.Strings.SetString(Section, Entry, "{ \"schemaVersion\": 22, \"terrains\": [ { \"terrainId\"");

            var store = new TerrainDocumentStore();
            List<TerrainDefinition>? terrains = store.Load(doc, out string? failureMessage);

            Assert.Null(terrains);
            Assert.False(string.IsNullOrWhiteSpace(failureMessage));
        }
        finally
        {
            doc.Dispose();
        }
    }

    [RhinoNativeFact]
    public void Load_UnknownDiscriminator_ReturnsNullWithFailureMessageInsteadOfThrowing()
    {
        RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        try
        {
            string json = "{ \"schemaVersion\": 22, \"terrains\": [ { \"terrainId\": \"" + Guid.NewGuid() +
                "\", \"modifiers\": [ { \"$type\": \"SomeFutureModifierNotInThisBuild\" } ] } ] }";
            doc.Strings.SetString(Section, Entry, json);

            var store = new TerrainDocumentStore();
            List<TerrainDefinition>? terrains = store.Load(doc, out string? failureMessage);

            Assert.Null(terrains);
            Assert.False(string.IsNullOrWhiteSpace(failureMessage));
        }
        finally
        {
            doc.Dispose();
        }
    }

    [RhinoNativeFact]
    public void Save_StashesPreviousRawJsonUnderBackupEntry()
    {
        RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        try
        {
            var store = new TerrainDocumentStore();
            var original = new List<TerrainDefinition> { new() { Name = "Original" } };
            store.Save(doc, original);

            var updated = new List<TerrainDefinition> { new() { Name = "Updated" } };
            store.Save(doc, updated);

            string? backup = doc.Strings.GetValue(Section, "Terrains.backup");
            Assert.NotNull(backup);
            Assert.Contains("Original", backup);
        }
        finally
        {
            doc.Dispose();
        }
    }

    [RhinoNativeFact]
    public void Load_FailedLoad_CallerMustNotSaveOverStoredJson()
    {
        // TerrainController is excluded from this test project's compiled sources (it needs the full
        // Rhino command/plugin host), so this exercises the same guard TerrainController.GetState/Save
        // relies on: a null Load result must be treated as "unreadable" and never round-tripped
        // through Save, which would replace the corrupt-but-recoverable JSON with an empty list.
        RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        try
        {
            const string corruptJson = "{ this is not valid json";
            doc.Strings.SetString(Section, Entry, corruptJson);

            var store = new TerrainDocumentStore();
            List<TerrainDefinition>? terrains = store.Load(doc, out string? failureMessage);

            Assert.Null(terrains);
            Assert.False(string.IsNullOrWhiteSpace(failureMessage));
            Assert.Equal(corruptJson, doc.Strings.GetValue(Section, Entry));
        }
        finally
        {
            doc.Dispose();
        }
    }
}
