namespace MoleHill.Rhino.Registry;

/// <summary>
/// The accent every card family's descriptor falls back to. Modifiers, analyses, annotations and objects
/// each declare an <c>AccentArgb</c>; a type that does not, or an unknown kind, shares this neutral grey.
/// </summary>
internal static class TypeAccent
{
    public const int NeutralArgb = unchecked((int)0xFF787878);
}
