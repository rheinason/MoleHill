using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// R01 regression coverage: a document written by a newer plugin must be refused before any legacy
/// rewriting runs, so the load-failure path preserves the original JSON instead of an older
/// serializer silently dropping the properties it does not know and stamping its own version back on.
/// Legacy (missing or zero) versions are the opposite case and must still load and migrate.
/// </summary>
public class TerrainSchemaVersionGuardTests
{
    private const string Section = "MoleHill.Rhino";
    private const string Entry = "Terrains";

    private static string TerrainJson(int? envelopeVersion, params int?[] terrainVersions)
    {
        string envelope = envelopeVersion is int version ? $"\"schemaVersion\": {version}, " : string.Empty;
        var terrains = new List<string>();
        foreach (int? terrainVersion in terrainVersions)
        {
            string stamp = terrainVersion is int value ? $"\"schemaVersion\": {value}, " : string.Empty;
            terrains.Add($"{{ {stamp}\"terrainId\": \"{Guid.NewGuid()}\", \"name\": \"Terrain\" }}");
        }

        return $"{{ {envelope}\"terrains\": [ {string.Join(", ", terrains)} ] }}";
    }

    [Fact]
    public void SupportedSchemaVersion_MatchesTerrainDefinitionVersion()
    {
        // The envelope stamp and the per-terrain stamp are bumped together; if they drift, a future
        // document can slip past one guard using the other's number.
        Assert.Equal(TerrainDefinition.CurrentSchemaVersion, TerrainSerializer.SupportedSchemaVersion);
    }

    [Fact]
    public void Deserialize_FutureEnvelopeVersion_Throws()
    {
        string json = TerrainJson(TerrainSerializer.SupportedSchemaVersion + 1, TerrainDefinition.CurrentSchemaVersion);

        var ex = Assert.Throws<NotSupportedException>(() => TerrainSerializer.Deserialize(json));
        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_FutureTerrainVersion_Throws()
    {
        string json = TerrainJson(TerrainSerializer.SupportedSchemaVersion, TerrainDefinition.CurrentSchemaVersion + 1);

        Assert.Throws<NotSupportedException>(() => TerrainSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_MixedVersionTerrainList_ThrowsWhenAnyTerrainIsFuture()
    {
        string json = TerrainJson(
            TerrainSerializer.SupportedSchemaVersion,
            27,
            TerrainDefinition.CurrentSchemaVersion,
            TerrainDefinition.CurrentSchemaVersion + 4);

        Assert.Throws<NotSupportedException>(() => TerrainSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_FutureEnvelopeWithNoTerrains_Throws()
    {
        // A future document with an empty list would otherwise return "no terrains" and let the next
        // save overwrite it.
        string json = $"{{ \"schemaVersion\": {TerrainSerializer.SupportedSchemaVersion + 1}, \"terrains\": [] }}";

        Assert.Throws<NotSupportedException>(() => TerrainSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_MissingSchemaVersions_TreatedAsLegacyAndAccepted()
    {
        string json = TerrainJson(null, (int?)null);

        TerrainDefinition terrain = Assert.Single(TerrainSerializer.Deserialize(json));
        Assert.Equal(TerrainDefinition.CurrentSchemaVersion, terrain.SchemaVersion);
    }

    [Fact]
    public void Deserialize_LegacySchemaVersion_StillMigratesAndRoundTrips()
    {
        string json = TerrainJson(22, 22);

        TerrainDefinition terrain = Assert.Single(TerrainSerializer.Deserialize(json));
        Assert.Equal(TerrainDefinition.CurrentSchemaVersion, terrain.SchemaVersion);

        // Re-serializing a migrated document and reading it back must be a no-op, not a second migration.
        string roundTripped = TerrainSerializer.Serialize(new[] { terrain });
        TerrainDefinition again = Assert.Single(TerrainSerializer.Deserialize(roundTripped));
        Assert.Equal(TerrainDefinition.CurrentSchemaVersion, again.SchemaVersion);
        Assert.Equal(roundTripped, TerrainSerializer.Serialize(new[] { again }));
    }

    [Fact]
    public void Deserialize_KnownTypeWithUnknownProperty_AtCurrentVersionStillLoads()
    {
        // Unknown properties are only a data-loss hazard when the document also claims a newer schema;
        // at the current version they are accepted so hand-edited or partially written data still opens.
        string json = $"{{ \"schemaVersion\": {TerrainSerializer.SupportedSchemaVersion}, \"terrains\": [ " +
            $"{{ \"schemaVersion\": {TerrainDefinition.CurrentSchemaVersion}, \"terrainId\": \"{Guid.NewGuid()}\", " +
            "\"name\": \"Terrain\", \"somePropertyThisBuildDoesNotKnow\": 42 } ] }";

        TerrainDefinition terrain = Assert.Single(TerrainSerializer.Deserialize(json));
        Assert.Equal("Terrain", terrain.Name);
    }

    [Fact]
    public void Deserialize_MalformedJson_ThrowsJsonException()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => TerrainSerializer.Deserialize("{ \"terrains\": ["));
    }

    [RhinoNativeFact]
    public void Load_FutureSchemaDocument_FailsWithoutOverwritingStoredJson()
    {
        RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        try
        {
            string futureJson = TerrainJson(
                TerrainSerializer.SupportedSchemaVersion + 1,
                TerrainDefinition.CurrentSchemaVersion + 1);
            doc.Strings.SetString(Section, Entry, futureJson);

            var store = new TerrainDocumentStore();
            List<TerrainDefinition>? terrains = store.Load(doc, out string? failureMessage);

            Assert.Null(terrains);
            Assert.False(string.IsNullOrWhiteSpace(failureMessage));
            Assert.Equal(futureJson, doc.Strings.GetValue(Section, Entry));
        }
        finally
        {
            doc.Dispose();
        }
    }
}
