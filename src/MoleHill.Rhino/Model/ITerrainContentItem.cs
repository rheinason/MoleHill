namespace MoleHill.Rhino.Model;

/// <summary>
/// The identity a card of any content family carries: who it is, what it is called, whether it is on.
///
/// This is deliberately <em>not</em> a shared base class. It says nothing about what a family's content
/// <em>is</em> or does — it exists so cross-cutting scaffolding that genuinely does not care (the build
/// loop's stage cache, fingerprinting, timing) can be written once instead of per family. Anything that
/// needs to know what the content means must still match on the concrete family type.
/// </summary>
public interface ITerrainContentItem
{
    Guid Id { get; }

    string Label { get; }

    bool IsEnabled { get; }
}
