namespace MoleHill.Rhino.Registry;

/// <summary>A row's extra button: what it says, its tooltip, and the key the panel runs it by.</summary>
internal sealed record RowAction(string Key, string Label, string Help);
