using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainPreviewView
{
    public required TerrainDefinition Terrain { get; init; }

    public required TerrainDisplayState DisplayState { get; init; }
}
