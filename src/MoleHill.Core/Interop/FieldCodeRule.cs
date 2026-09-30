namespace MoleHill.Core.Interop;

/// <summary>
/// One row of the field code table: what a surveyed code means, and where its output goes.
///
/// Serialized as-is to the per-user table file, so every member is a plain JSON-representable value.
/// </summary>
public sealed class FieldCodeRule
{
    /// <summary>The code as the crew types it, without any figure number or marker. Matched case-insensitively.</summary>
    public string Code { get; set; } = string.Empty;

    public FieldCodeRole Role { get; set; } = FieldCodeRole.Breakline;

    /// <summary>
    /// Layer this code's output is created on, relative to the survey's own layer (named after the
    /// imported file), e.g. "Edge of Pavement". Blank follows the role's default sublayer.
    ///
    /// A layer and not a <c>LayerRole</c>: layer roles route <i>generated terrain output</i>, whose
    /// appearance the terrain owns so preview and bake cannot drift. Survey linework is a user-owned
    /// input that the user then edits, moves and reassigns — claiming it for the layer template would be
    /// claiming ownership this command does not have.
    /// </summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>A human note shown in the editor. Never parsed.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Whether runs of this code close into a loop without needing a close marker.
    ///
    /// On for a code that is always an outline — a building footprint, a pond edge — so the surveyor does
    /// not have to remember a marker for the case that is never anything else.
    /// </summary>
    public bool ClosedByDefault { get; set; }

    public FieldCodeRule Clone() => new()
    {
        Code = Code,
        Role = Role,
        Layer = Layer,
        Description = Description,
        ClosedByDefault = ClosedByDefault
    };
}
