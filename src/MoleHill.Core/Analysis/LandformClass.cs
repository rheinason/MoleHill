namespace MoleHill.Core.Analysis;

/// <summary>
/// The landform a face belongs to, read from the surface's curvature at the analysis's landform size. In
/// legend order: the convex forms, then planar, then the mixed form, then the concave forms.
/// </summary>
/// <remarks>
/// <para>Eight classes, from two questions. First the <em>shape</em>, from the two principal curvatures
/// against the planar threshold: convex both ways, concave both ways, convex one way and concave the
/// other, curved one way only, or neither (Wood 1996's morphometric classes: peak, pit, pass, ridge,
/// channel, plane). Second, only for ground curved one way, the <em>orientation</em>: whether the bend runs
/// across the fall line (ridge, valley: water spreads or gathers) or along it (shoulder, toe: the slope
/// steepens or eases), which is the plan-by-profile reading of hillslope form (Ruhe; Dalrymple's
/// nine-unit slope).</para>
/// <para>Wood's "pass" is called <see cref="Saddle"/> and his "pit" is folded into <see cref="Hollow"/>:
/// a closed dip and a scoop in a slope are both ground concave every way, and both hold or gather
/// water.</para>
/// </remarks>
public enum LandformClass
{
    /// <summary>Convex every way: a summit, knoll, mound or the rounded nose of a spur.</summary>
    Peak = 0,

    /// <summary>Convex across the fall line only, or along a level crest: a ridge, spur or berm crest.
    /// Water runs off it to both sides.</summary>
    Ridge = 1,

    /// <summary>Convex along the fall line only: the top of a slope, where it steepens over an edge.</summary>
    Shoulder = 2,

    /// <summary>Bends less than the planar threshold in every direction: a plane, level or sloping.</summary>
    Planar = 3,

    /// <summary>Convex one way and concave the other: a col, pass or the neck between two rises.</summary>
    Saddle = 4,

    /// <summary>Concave along the fall line only: the foot of a slope, where it eases out.</summary>
    Toe = 5,

    /// <summary>Concave across the fall line only, or along a level floor: a valley, channel or swale.
    /// Water gathers into it from both sides.</summary>
    Valley = 6,

    /// <summary>Concave every way: a bowl, pit, depression or the scooped head of a valley.</summary>
    Hollow = 7
}
