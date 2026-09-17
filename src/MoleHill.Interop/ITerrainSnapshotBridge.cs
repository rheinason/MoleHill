using Rhino;

namespace MoleHill.Interop;

public interface ITerrainSnapshotBridge
{
    int ContractVersion { get; }

    event EventHandler? SnapshotChanged;

    TerrainInteropSnapshot? GetSnapshot(RhinoDoc doc, string? terrainKey, out string? errorMessage);

    string GetStatus(RhinoDoc doc, string? terrainKey, out string? message);

    string? ResolveReferenceKey(RhinoDoc doc, string? terrainKey);

    IReadOnlyList<TerrainInteropReference> GetReferences(RhinoDoc doc);

    string? GetDocumentIdentity(RhinoDoc doc);

    string EnsureDocumentIdentity(RhinoDoc doc);
}
