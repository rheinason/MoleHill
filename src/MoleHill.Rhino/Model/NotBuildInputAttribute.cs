namespace MoleHill.Rhino.Model;

/// <summary>
/// Marks a definition property that no build reads: presentation (a card's name, a preview toggle whose
/// overlay is rebuilt every build anyway) or a result the build writes back for the card to show. Stage
/// fingerprints leave these out, so changing one never re-runs a stage. Renaming the Remesh card used to
/// re-run Remesh (37 s at the 1 m park), and a stair re-ran once after every build because its own
/// computed summary had changed its fingerprint.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class NotBuildInputAttribute : Attribute
{
}
