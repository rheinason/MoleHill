using Rhino.Geometry;

namespace MoleHill.Interop;

/// <summary>
/// A validated, Revit-neutral Toposolid preparation package, as produced by Grasshopper's
/// <c>Prepare Toposolid</c>. It lives here because the producer (<c>MoleHill.gha</c>) and the Revit
/// writer (<c>MoleHill.Revit.gha</c>) are separate assemblies that share only this one: a type defined
/// in either would be invisible to the other at runtime. Coordinates are in Rhino model units, already
/// placed; <see cref="MetersPerModelUnit"/> is the single unit conversion a consumer applies.
/// </summary>
public interface IToposolidPreparation
{
    /// <summary>Closed horizontal outer and hole profiles for the base Toposolid.</summary>
    IReadOnlyList<Curve> Profiles { get; }

    IReadOnlyList<Point3d> ElevationPoints { get; }

    IReadOnlyList<IToposolidSubdivision> SubdivisionProfiles { get; }

    string Name { get; }

    /// <summary>Stable terrain key; the identity a downstream element is matched by.</summary>
    string Key { get; }

    /// <summary>Deterministic fingerprint of the prepared profiles and points; unchanged means no-op.</summary>
    string GeometryFingerprint { get; }

    double MetersPerModelUnit { get; }
}

/// <summary>One named subdivision region of a <see cref="IToposolidPreparation"/>.</summary>
public interface IToposolidSubdivision
{
    string Name { get; }

    string Key { get; }

    IReadOnlyList<Curve> Profiles { get; }

    string GeometryFingerprint { get; }
}
