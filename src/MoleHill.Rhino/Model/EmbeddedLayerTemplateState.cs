namespace MoleHill.Rhino.Model;

/// <summary>
/// The layer templates a document carries with it.
///
/// Templates are otherwise a per-user setting, which is right for an office standard but wrong for a
/// drawing: a .3dm opened on another machine would pick up whatever templates that machine happened
/// to have, and render differently. So the document keeps a resolved copy of every template it uses,
/// and that copy is what output routes and styles by. The per-user file remains the standard new
/// documents are seeded from, and the two are only ever reconciled by an explicit action.
/// </summary>
public sealed class EmbeddedLayerTemplateState
{
    public int Version { get; set; } = 1;

    /// <summary>Template used by terrains that do not name one of their own.</summary>
    public string ActiveName { get; set; } = "MoleHill Terrain";

    public List<EmbeddedLayerTemplate> Templates { get; set; } = new();
}

public sealed class EmbeddedLayerTemplate
{
    public LayerTemplateDefinition Template { get; set; } = new();

    /// <summary>
    /// Fingerprint of the machine-local template of this name at the moment it was copied in. Lets
    /// the panel say whether the document has since diverged from the local standard without having
    /// to diff the whole thing, and without either side silently overwriting the other.
    /// </summary>
    public ulong LocalFingerprintWhenEmbedded { get; set; }

    public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;
}
