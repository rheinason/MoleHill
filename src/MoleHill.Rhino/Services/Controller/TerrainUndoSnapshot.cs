namespace MoleHill.Rhino.Services;

/// <summary>
/// Serialized private plug-in state carried by one Rhino custom undo event. The action description is
/// metadata; state equality deliberately ignores it so an unchanged edit does not create an undo item.
/// </summary>
internal sealed record TerrainUndoSnapshot
{
    public string Json { get; init; } = string.Empty;
    public Guid? SelectedTerrainId { get; init; }
    public string Description { get; init; } = "Edit MoleHill Terrain";

    public bool HasSameState(TerrainUndoSnapshot other) =>
        SelectedTerrainId == other.SelectedTerrainId &&
        string.Equals(Json, other.Json, StringComparison.Ordinal);

    public TerrainUndoSnapshot WithDescription(string description) => this with
    {
        Description = description
    };
}
