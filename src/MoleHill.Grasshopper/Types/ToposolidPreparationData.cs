// Revit-neutral, validated preparation package for a downstream Rhino.Inside.Revit Toposolid creator.
using MoleHill.Interop;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Types;

public sealed class ToposolidPreparationData : IToposolidPreparation
{
    public ToposolidPreparationData(
        IEnumerable<Curve> profiles,
        IEnumerable<Point3d> elevationPoints,
        IEnumerable<ToposolidSubdivisionData>? subdivisions,
        IEnumerable<Curve>? breaklines,
        string? name,
        string? key,
        long revision,
        string? geometryFingerprint,
        string? unitSystem,
        double metersPerModelUnit,
        int sourcePointCount,
        double maximumMeasuredVerticalError,
        IEnumerable<string>? diagnostics)
    {
        Profiles = profiles.Select(profile => profile.DuplicateCurve()).ToArray();
        ElevationPoints = elevationPoints.ToArray();
        Subdivisions = (subdivisions ?? Array.Empty<ToposolidSubdivisionData>())
            .Select(subdivision => subdivision.Duplicate())
            .ToArray();
        Breaklines = (breaklines ?? Array.Empty<Curve>())
            .Select(breakline => breakline.DuplicateCurve())
            .ToArray();
        Name = string.IsNullOrWhiteSpace(name) ? "Terrain" : name;
        Key = key ?? string.Empty;
        Revision = revision;
        GeometryFingerprint = geometryFingerprint ?? string.Empty;
        UnitSystem = string.IsNullOrWhiteSpace(unitSystem) ? "Unspecified" : unitSystem;
        MetersPerModelUnit = metersPerModelUnit;
        SourcePointCount = sourcePointCount;
        MaximumMeasuredVerticalError = maximumMeasuredVerticalError;
        Diagnostics = (diagnostics ?? Array.Empty<string>())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();
    }

    public IReadOnlyList<Curve> Profiles { get; }

    public IReadOnlyList<Point3d> ElevationPoints { get; }

    public IReadOnlyList<ToposolidSubdivisionData> Subdivisions { get; }

    IReadOnlyList<IToposolidSubdivision> IToposolidPreparation.SubdivisionProfiles => Subdivisions;

    /// <summary>Transformed source constraints. Revit creation does not guarantee them as TIN edges.</summary>
    public IReadOnlyList<Curve> Breaklines { get; }

    public string Name { get; }

    public string Key { get; }

    public long Revision { get; }

    public string GeometryFingerprint { get; }

    public string UnitSystem { get; }

    public double MetersPerModelUnit { get; }

    public int SourcePointCount { get; }

    public double MaximumMeasuredVerticalError { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public bool IsValid =>
        Profiles.Count > 0 &&
        ElevationPoints.Count >= 3 &&
        ElevationPoints.All(point => point.IsValid) &&
        !string.IsNullOrWhiteSpace(GeometryFingerprint) &&
        double.IsFinite(MetersPerModelUnit) &&
        MetersPerModelUnit > 0.0;

    public ToposolidPreparationData Duplicate() => new(
        Profiles,
        ElevationPoints,
        Subdivisions,
        Breaklines,
        Name,
        Key,
        Revision,
        GeometryFingerprint,
        UnitSystem,
        MetersPerModelUnit,
        SourcePointCount,
        MaximumMeasuredVerticalError,
        Diagnostics);
}
