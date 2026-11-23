using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>What importing a terrain definition file will create, shown before anything is added.</summary>
internal sealed record TerrainImportPreview(
    TerrainDefinition Terrain,
    IReadOnlyList<string> FoundLayers,
    IReadOnlyList<string> MissingLayers,
    int ClearedTerrainLinks,
    string SourceUnits);
