// One bend of an edited long section: a station, an elevation, and the original bend it started as.
namespace MoleHill.Core.Analysis;

/// <summary>
/// A bend (vertical intersection point) of a <see cref="ProfileEditor"/> profile. <see cref="OriginalIndex"/>
/// is the index of the bend this one started as in <see cref="ProfileEditor.InitialBends"/>, or -1 for a bend
/// the user added; it is what lets an edit leave every stretch it did not touch exactly as it was.
/// </summary>
public readonly record struct ProfileBend(double Station, double Elevation, int OriginalIndex)
{
    public bool IsAdded => OriginalIndex < 0;
}
