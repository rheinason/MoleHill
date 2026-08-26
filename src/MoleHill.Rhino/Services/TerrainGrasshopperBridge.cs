// Public event and snapshot bridge consumed by the optional Grasshopper host through reflection.
using Rhino;

namespace MoleHill.Rhino.Services;

public static class TerrainGrasshopperBridge
{
    static TerrainGrasshopperBridge()
    {
        TerrainController.Instance.StateChanged += OnTerrainStateChanged;
    }

    public static event EventHandler? SnapshotChanged;

    public static TerrainInteropSnapshot? GetSnapshot(
        RhinoDoc doc,
        string? terrainKey,
        out string? errorMessage)
    {
        return TerrainController.Instance.TryCreateGrasshopperSnapshot(doc, terrainKey, out errorMessage);
    }

    private static void OnTerrainStateChanged(object? sender, EventArgs e)
    {
        SnapshotChanged?.Invoke(null, EventArgs.Empty);
    }
}
