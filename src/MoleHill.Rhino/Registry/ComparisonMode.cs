namespace MoleHill.Rhino.Registry;

/// <summary>What a comparing card measures or draws against (design review Steps 29, 66, 68).</summary>
internal enum ComparisonMode
{
    /// <summary>Sections only: no reference drawn and no cut/fill shading.</summary>
    Off,

    /// <summary>This terrain's own surface before its modifiers moved it.</summary>
    Original,

    /// <summary>Another MoleHill terrain in the document.</summary>
    Terrain,

    /// <summary>Rhino geometry: a survey mesh or surface.</summary>
    Geometry,
}
