// Revit-free validation and planning for Write Toposolids: everything that can be decided without a Revit host.
using Rhino.Geometry;

namespace MoleHill.Revit.Planning;

/// <summary>One preparation package, reduced to the values the writer reads.</summary>
internal sealed record PreparationInput(
    string Key,
    string Name,
    string Fingerprint,
    double MetersPerUnit,
    IReadOnlyList<Point3d[]?> Profiles,
    IReadOnlyList<Point3d> Points,
    IReadOnlyList<SubdivisionInput> Subdivisions);

/// <summary>
/// A profile is its polyline vertices, closing vertex repeated; one the caller could not read as a polyline
/// is passed as null and rejected here. Plain points, not <see cref="Polyline"/>, whose members call native
/// Rhino and would keep this planner out of the managed test lane.
/// </summary>
internal sealed record SubdivisionInput(string Key, string Name, string Fingerprint, IReadOnlyList<Point3d[]?> Profiles);

/// <summary>A closed loop in Revit internal feet; the closing vertex is not repeated.</summary>
internal sealed record PlannedLoop(IReadOnlyList<Point3d> Vertices);

internal sealed record PlannedSubdivision(string IdentityKey, string Name, string Fingerprint, IReadOnlyList<PlannedLoop> Loops);

internal sealed record PlannedToposolid(
    string Key,
    string Name,
    string Fingerprint,
    IReadOnlyList<PlannedLoop> Loops,
    IReadOnlyList<Point3d> Points,
    IReadOnlyList<PlannedSubdivision> Subdivisions);

internal enum ToposolidWriteAction
{
    Keep,
    Create,
    Replace
}

internal static class ToposolidWritePlanner
{
    public const double MetersPerFoot = 0.3048;

    /// <summary>How far apart, in model units, a polyline's ends may be and still count as closed.</summary>
    private const double ClosureTolerance = 1e-9;

    /// <summary>
    /// The key a subdivision is stored under. Subdivision keys come from zone keys, which repeat across
    /// the pieces of a partitioned terrain, so they are only unique beneath their host.
    /// </summary>
    public static string SubdivisionIdentityKey(string hostKey, string subdivisionKey) => $"{hostKey}::{subdivisionKey}";

    /// <summary>
    /// Validates every package and converts it to Revit internal feet. Nothing is planned unless every
    /// package is valid: a write is one transaction, so a partial plan would only be rolled back.
    /// </summary>
    public static bool TryPlan(
        IReadOnlyList<PreparationInput> inputs,
        double shortCurveToleranceFeet,
        out List<PlannedToposolid> plans,
        out List<string> errors)
    {
        plans = new List<PlannedToposolid>(inputs.Count);
        errors = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < inputs.Count; index++)
        {
            PreparationInput input = inputs[index];
            string label = string.IsNullOrWhiteSpace(input.Key) ? $"Preparation {index}" : input.Key;
            int errorCount = errors.Count;

            if (string.IsNullOrWhiteSpace(input.Key))
                errors.Add($"{label}: the terrain key is empty, so the Toposolid could not be matched on the next run. " +
                           "A bare mesh has no key: prepare a MoleHill Terrain from Terrain Snapshot, or from Construct Terrain with a Key.");
            else if (!keys.Add(input.Key))
                errors.Add($"{label}: the terrain key appears more than once; give each partition its own key.");

            if (string.IsNullOrWhiteSpace(input.Fingerprint))
                errors.Add($"{label}: the geometry fingerprint is empty.");

            if (!double.IsFinite(input.MetersPerUnit) || input.MetersPerUnit <= 0.0)
                errors.Add($"{label}: Meters Per Unit must be positive, not {input.MetersPerUnit}.");

            if (input.Points.Count < 3)
                errors.Add($"{label}: a Toposolid needs at least three elevation points, not {input.Points.Count}.");

            if (errors.Count > errorCount)
                continue;

            double scale = input.MetersPerUnit / MetersPerFoot;
            List<PlannedLoop>? loops = TryConvertLoops(input.Profiles, scale, shortCurveToleranceFeet, label, "profile", errors);
            if (loops != null && loops.Count == 0)
                errors.Add($"{label}: the preparation has no profile.");

            var subdivisions = new List<PlannedSubdivision>(input.Subdivisions.Count);
            var subdivisionKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (SubdivisionInput subdivision in input.Subdivisions)
            {
                string subdivisionLabel = $"{label} / {subdivision.Name}";
                if (string.IsNullOrWhiteSpace(subdivision.Key) || !subdivisionKeys.Add(subdivision.Key))
                {
                    errors.Add($"{subdivisionLabel}: the subdivision key is empty or repeated.");
                    continue;
                }

                List<PlannedLoop>? subdivisionLoops = TryConvertLoops(
                    subdivision.Profiles, scale, shortCurveToleranceFeet, subdivisionLabel, "subdivision profile", errors);
                if (subdivisionLoops == null)
                    continue;
                if (subdivisionLoops.Count == 0)
                {
                    errors.Add($"{subdivisionLabel}: the subdivision has no profile.");
                    continue;
                }

                subdivisions.Add(new PlannedSubdivision(
                    SubdivisionIdentityKey(input.Key, subdivision.Key),
                    subdivision.Name,
                    subdivision.Fingerprint,
                    subdivisionLoops));
            }

            if (errors.Count > errorCount || loops == null)
                continue;

            plans.Add(new PlannedToposolid(
                input.Key,
                input.Name,
                input.Fingerprint,
                loops,
                input.Points.Select(point => Scale(point, scale)).ToArray(),
                subdivisions));
        }

        if (errors.Count > 0)
            plans.Clear();
        return errors.Count == 0;
    }

    /// <summary>An unchanged fingerprint is a no-op; anything else creates or replaces.</summary>
    public static ToposolidWriteAction Decide(bool exists, string? existingFingerprint, string fingerprint)
    {
        if (!exists)
            return ToposolidWriteAction.Create;
        return string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal)
            ? ToposolidWriteAction.Keep
            : ToposolidWriteAction.Replace;
    }

    /// <summary>
    /// A subdivision follows its host, so a replaced host means a new subdivision even when the
    /// subdivision's own profile has not changed.
    /// </summary>
    public static ToposolidWriteAction DecideSubdivision(
        bool exists,
        string? existingFingerprint,
        bool onCurrentHost,
        string fingerprint)
    {
        if (!exists)
            return ToposolidWriteAction.Create;
        return onCurrentHost && string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal)
            ? ToposolidWriteAction.Keep
            : ToposolidWriteAction.Replace;
    }

    private static List<PlannedLoop>? TryConvertLoops(
        IReadOnlyList<Point3d[]?> profiles,
        double scale,
        double shortCurveToleranceFeet,
        string label,
        string noun,
        List<string> errors)
    {
        var loops = new List<PlannedLoop>(profiles.Count);
        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            Point3d[]? polyline = profiles[profileIndex];
            if (polyline == null || polyline.Length < 4 || polyline[0].DistanceTo(polyline[^1]) > ClosureTolerance)
            {
                errors.Add($"{label}: {noun} {profileIndex} is not a closed polyline.");
                return null;
            }

            var vertices = new Point3d[polyline.Length - 1];
            for (int vertex = 0; vertex < vertices.Length; vertex++)
                vertices[vertex] = Scale(polyline[vertex], scale);

            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                Point3d next = vertices[(vertex + 1) % vertices.Length];
                if (vertices[vertex].DistanceTo(next) <= shortCurveToleranceFeet)
                {
                    errors.Add($"{label}: {noun} {profileIndex} has a segment shorter than Revit's short-curve tolerance.");
                    return null;
                }
            }

            loops.Add(new PlannedLoop(vertices));
        }

        return loops;
    }

    private static Point3d Scale(Point3d point, double scale) => new(point.X * scale, point.Y * scale, point.Z * scale);
}
