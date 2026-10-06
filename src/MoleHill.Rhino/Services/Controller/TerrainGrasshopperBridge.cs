// Public event and snapshot bridge consumed by the optional Grasshopper host through reflection.
using Rhino;
using MoleHill.Interop;

namespace MoleHill.Rhino.Services;

public sealed class TerrainGrasshopperBridge : ITerrainSnapshotBridge
{
    private TerrainGrasshopperBridge()
    {
        TerrainController.Instance.StateChanged += OnTerrainStateChanged;
    }

    public static TerrainGrasshopperBridge Instance { get; } = new();

    public int ContractVersion => 4;

    public event EventHandler? SnapshotChanged;

    public TerrainInteropSnapshot? GetSnapshot(
        RhinoDoc doc,
        string? terrainKey,
        out string? errorMessage)
    {
        return TerrainController.Instance.TryCreateGrasshopperSnapshot(doc, terrainKey, out errorMessage);
    }

    public IReadOnlyList<TerrainInteropReference> GetReferences(RhinoDoc doc) =>
        TerrainController.Instance.GetTerrains(doc)
            .Select(terrain => new TerrainInteropReference(terrain.Name, terrain.TerrainId.ToString("D")))
            .ToArray();

    public string GetStatus(RhinoDoc doc, string? terrainKey, out string? message) =>
        TerrainController.Instance.GetGrasshopperSnapshotStatus(doc, terrainKey, out message);

    public string? ResolveReferenceKey(RhinoDoc doc, string? terrainKey) =>
        TerrainController.Instance.ResolveGrasshopperReferenceKey(doc, terrainKey);

    public string? GetDocumentIdentity(RhinoDoc doc) => TerrainDocumentIdentity.Get(doc);

    public string EnsureDocumentIdentity(RhinoDoc doc) => TerrainDocumentIdentity.Ensure(doc);

    private void OnTerrainStateChanged(object? sender, EventArgs e)
    {
        SnapshotChanged?.Invoke(null, EventArgs.Empty);
    }
}
