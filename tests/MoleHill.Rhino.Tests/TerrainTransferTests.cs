using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Moving a terrain definition between documents (design review Steps 77-78): only layer paths travel,
/// everything document-bound is left behind, and the import is a new terrain in the document's units.
/// </summary>
public class TerrainTransferTests
{
    private static readonly ModelUnitContext Meters = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);

    private static TerrainDefinition Sample(out Guid otherTerrain, out Guid zoneId)
    {
        otherTerrain = Guid.NewGuid();
        var terrain = new TerrainDefinition { Name = "Site" };

        var triangulate = new TriangulateModifierDefinition();
        triangulate.Points.ReplaceObjects(new[] { Guid.NewGuid(), Guid.NewGuid() });
        triangulate.Points.ReplaceLayers(new[] { "Survey::Points" });
        triangulate.Breaklines.ReplaceLayers(new[] { "Survey::Kerbs" });
        terrain.Modifiers.Add(triangulate);

        terrain.Analyses.Add(new EarthworkAnalysisDefinition { ReferenceTerrainId = otherTerrain });

        var zone = new CollageZoneDefinition { Name = "Lawn" };
        zone.Boundaries.ReplaceObjects(new[] { Guid.NewGuid() });
        zone.Boundaries.ReplaceLayers(new[] { "Zones::Lawn" });
        terrain.Zones.Add(zone);
        zoneId = zone.ZoneId;

        var scatter = new ScatterObjectDefinition { ZoneIds = new List<Guid> { zone.ZoneId } };
        terrain.Objects.Add(scatter);

        terrain.Annotations.Add(new LongitudinalSectionAnnotationDefinition { SampleInterval = 1000.0 });
        terrain.OutputObjectIds.Add(Guid.NewGuid());
        return terrain;
    }

    [Fact]
    public void Export_KeepsLayerPaths_AndLeavesPickedObjectsAndTerrainLinksBehind()
    {
        TerrainDefinition terrain = Sample(out _, out _);

        TerrainImportPreview preview = TerrainTransfer.Read(
            TerrainTransfer.Export(terrain, UnitSystem.Meters), Meters, _ => true);
        TerrainDefinition imported = preview.Terrain;

        Assert.All(TerrainTransfer.SourceSets(imported), set => Assert.Empty(set.ObjectIds));
        Assert.Equal(
            new[] { "Survey::Kerbs", "Survey::Points", "Zones::Lawn" },
            preview.FoundLayers.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Null(imported.Analyses.OfType<EarthworkAnalysisDefinition>().Single().ReferenceTerrainId);
        Assert.Empty(imported.OutputObjectIds);

        // The source terrain is untouched by exporting it.
        Assert.Equal(2, terrain.Modifiers.OfType<TriangulateModifierDefinition>().Single().Points.ObjectIds.Count);
    }

    [Fact]
    public void Import_IsANewTerrain_WithItsZoneFiltersStillConnected()
    {
        TerrainDefinition terrain = Sample(out _, out Guid oldZone);

        TerrainDefinition imported = TerrainTransfer.Read(
            TerrainTransfer.Export(terrain, UnitSystem.Meters), Meters, _ => true).Terrain;

        Assert.NotEqual(terrain.TerrainId, imported.TerrainId);
        Assert.NotEqual(terrain.Modifiers[0].Id, imported.Modifiers[0].Id);
        Guid newZone = Assert.Single(imported.Zones).ZoneId;
        Assert.NotEqual(oldZone, newZone);
        Assert.Equal(new[] { newZone }, imported.Objects.OfType<ScatterObjectDefinition>().Single().ZoneIds);
    }

    [Fact]
    public void Import_ConvertsLengthsToTheDocumentsUnits_AndSortsLayersByPresence()
    {
        TerrainDefinition terrain = Sample(out _, out _);
        string millimetreFile = TerrainTransfer.Export(terrain, UnitSystem.Millimeters);

        TerrainImportPreview preview = TerrainTransfer.Read(millimetreFile, Meters, path => path.StartsWith("Survey", StringComparison.Ordinal));

        Assert.Equal(1.0, preview.Terrain.Annotations.OfType<LongitudinalSectionAnnotationDefinition>().Single().SampleInterval, 9);
        Assert.Equal(new[] { "Zones::Lawn" }, preview.MissingLayers);
        Assert.Equal(0, preview.ClearedTerrainLinks); // export already left the link behind
    }

    [Fact]
    public void Read_AHandEditedFileWithTerrainLinks_ClearsAndCountsThem()
    {
        TerrainDefinition terrain = Sample(out Guid other, out _);
        string json = TerrainTransfer.Export(terrain, UnitSystem.Meters)
            .Replace("\"referenceTerrainId\": null", $"\"referenceTerrainId\": \"{other}\"", StringComparison.Ordinal);
        Assert.Contains(other.ToString(), json);

        TerrainImportPreview preview = TerrainTransfer.Read(json, Meters, _ => true);

        Assert.Equal(1, preview.ClearedTerrainLinks);
        Assert.Null(preview.Terrain.Analyses.OfType<EarthworkAnalysisDefinition>().Single().ReferenceTerrainId);
    }

    [Fact]
    public void Read_SomethingElse_SaysItIsNotATerrainDefinition()
    {
        Assert.Throws<InvalidDataException>(() => TerrainTransfer.Read("{\"hello\": 1}", Meters, _ => true));
        Assert.Throws<InvalidDataException>(() => TerrainTransfer.Read("not json", Meters, _ => true));
    }

    [Fact]
    public void Read_ANewerSchema_OrADamagedUnit_SaysSoInsteadOfThrowingSomethingElse()
    {
        string json = TerrainTransfer.Export(Sample(out _, out _), UnitSystem.Meters);
        string newer = System.Text.RegularExpressions.Regex.Replace(json, "\"schemaVersion\": \\d+", "\"schemaVersion\": 9999");
        string badUnit = json.Replace("\"unitSystem\": \"Meters\"", "\"unitSystem\": 3", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => TerrainTransfer.Read(newer, Meters, _ => true));
        Assert.Contains("newer version", exception.Message);
        Assert.Equal(1.0, TerrainTransfer.Read(badUnit, Meters, _ => true).Terrain.Annotations
            .OfType<LongitudinalSectionAnnotationDefinition>().Single().SampleInterval / 1000.0, 9);
    }

    [Fact]
    public void ClearTerrainLinks_CountsARemovedProfileColour()
    {
        Guid other = Guid.NewGuid();
        var terrain = new TerrainDefinition();
        var section = new TerrainSectionAnnotationDefinition();
        section.ProfileColorArgbs[other] = unchecked((int)0xFF00FF00);
        terrain.Annotations.Add(section);

        Assert.Equal(1, TerrainTransfer.ClearTerrainLinks(terrain, id => id == other));
        Assert.Empty(section.ProfileColorArgbs);
    }
}
