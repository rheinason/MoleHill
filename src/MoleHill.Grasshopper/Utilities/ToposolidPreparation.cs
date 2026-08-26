// Revit-neutral Toposolid profile validation and bounded elevation-point preparation in incoming coordinates.
using MoleHill.Core.Interop;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MoleHill.Grasshopper.Utilities;

internal static class ToposolidPreparation
{
    internal sealed class Options
    {
        public int MaximumPointCount { get; init; } = 20_000;

        /// <summary>Maximum measured reconstruction error in Rhino model units. Zero selects 0.05 m.</summary>
        public double VerticalTolerance { get; init; }

        /// <summary>Profile and XY deduplication tolerance in source model units.</summary>
        public double SourceTolerance { get; init; } = 0.001;
    }

    private readonly record struct CellKey(long X, long Y);

    private sealed class PointAccumulator
    {
        private readonly double _tolerance;
        private readonly double _inverseTolerance;
        private readonly Dictionary<CellKey, List<int>> _cells = new();

        public PointAccumulator(double tolerance)
        {
            _tolerance = Math.Max(tolerance, 1e-9);
            _inverseTolerance = 1.0 / _tolerance;
        }

        public List<Point3d> Points { get; } = new();

        public bool TryAdd(Point3d point, out string? error)
        {
            error = null;
            if (!point.IsValid)
            {
                error = "Encountered a non-finite terrain coordinate.";
                return false;
            }

            CellKey cell = ToCell(point);
            for (long x = cell.X - 1; x <= cell.X + 1; x++)
            {
                for (long y = cell.Y - 1; y <= cell.Y + 1; y++)
                {
                    if (!_cells.TryGetValue(new CellKey(x, y), out List<int>? candidates))
                        continue;

                    foreach (int index in candidates)
                    {
                        Point3d existing = Points[index];
                        if (Math.Abs(existing.X - point.X) > _tolerance ||
                            Math.Abs(existing.Y - point.Y) > _tolerance)
                            continue;

                        if (Math.Abs(existing.Z - point.Z) > _tolerance * 4.0)
                        {
                            error = $"Terrain is not a single-valued heightfield near XY ({point.X:G10}, {point.Y:G10}); " +
                                    $"elevations {existing.Z:G10} and {point.Z:G10} conflict.";
                            return false;
                        }

                        return true;
                    }
                }
            }

            int newIndex = Points.Count;
            Points.Add(point);
            if (!_cells.TryGetValue(cell, out List<int>? list))
            {
                list = new List<int>(1);
                _cells[cell] = list;
            }

            list.Add(newIndex);
            return true;
        }

        public bool Contains(Point3d point)
        {
            CellKey cell = ToCell(point);
            for (long x = cell.X - 1; x <= cell.X + 1; x++)
            {
                for (long y = cell.Y - 1; y <= cell.Y + 1; y++)
                {
                    if (!_cells.TryGetValue(new CellKey(x, y), out List<int>? candidates))
                        continue;
                    foreach (int index in candidates)
                    {
                        Point3d existing = Points[index];
                        if (Math.Abs(existing.X - point.X) <= _tolerance &&
                            Math.Abs(existing.Y - point.Y) <= _tolerance)
                            return true;
                    }
                }
            }

            return false;
        }

        private CellKey ToCell(Point3d point) => new(
            (long)Math.Floor(point.X * _inverseTolerance),
            (long)Math.Floor(point.Y * _inverseTolerance));
    }

    public static bool TryPrepare(
        MoleHillTerrainData terrain,
        Options options,
        out ToposolidPreparationData? preparation,
        out IReadOnlyList<string> report)
    {
        preparation = null;
        var messages = new List<string>();
        report = messages;

        if (!terrain.IsValid)
        {
            messages.Add("Terrain mesh is empty or invalid.");
            return false;
        }

        if (string.Equals(terrain.UnitSystem, "Unspecified", StringComparison.OrdinalIgnoreCase) ||
            !double.IsFinite(terrain.MetersPerModelUnit) ||
            terrain.MetersPerModelUnit <= 0.0)
        {
            messages.Add("Terrain unit metadata is missing. Set Rhino document units or reconstruct the terrain with explicit unit metadata.");
            return false;
        }

        if (options.MaximumPointCount is < 3 or > 50_000)
        {
            messages.Add("Maximum Points must be between 3 and Revit's documented 50,000 upper threshold.");
            return false;
        }

        Transform sourceToOutput = Transform.Identity;
        double sourceTolerance = options.SourceTolerance > 0.0 ? options.SourceTolerance : 0.001;
        double outputTolerance = Math.Max(sourceTolerance, 1e-9);
        double verticalTolerance = options.VerticalTolerance > 0.0
            ? options.VerticalTolerance
            : 0.05 / terrain.MetersPerModelUnit;

        Point3d[] sourceVertices = terrain.Mesh.Vertices
            .Select(vertex => TransformPoint(new Point3d(vertex), sourceToOutput))
            .ToArray();
        if (sourceVertices.Any(point => !point.IsValid))
        {
            messages.Add("Terrain contains a non-finite vertex.");
            return false;
        }
        double profileElevation = sourceVertices.Min(point => point.Z);

        if (ContainsNearVerticalFace(terrain.Mesh, sourceToOutput, outputTolerance))
        {
            messages.Add("Terrain contains a vertical or near-vertical face and cannot be represented as one Toposolid top face.");
            return false;
        }

        var sourceHeightfield = new PointAccumulator(outputTolerance);
        foreach (Point3d point in sourceVertices)
        {
            if (!sourceHeightfield.TryAdd(point, out string? heightfieldError))
            {
                messages.Add(heightfieldError!);
                return false;
            }
        }

        Polyline[] nakedEdges = terrain.Mesh.GetNakedEdges() ?? Array.Empty<Polyline>();
        if (nakedEdges.Length == 0)
        {
            messages.Add("Terrain has no closed naked boundary from which to create a Toposolid profile.");
            return false;
        }

        var profiles = new List<Curve>(nakedEdges.Length);
        var criticalPoints = new PointAccumulator(outputTolerance);
        foreach (Polyline edge in nakedEdges)
        {
            if (!edge.IsClosed || edge.Count < 4)
            {
                messages.Add("Terrain contains an open or degenerate naked boundary.");
                DisposeCurves(profiles);
                return false;
            }

            var flattened = new Polyline(edge.Count);
            foreach (Point3d sourcePoint in edge)
            {
                Point3d transformed = TransformPoint(sourcePoint, sourceToOutput);
                if (!criticalPoints.TryAdd(transformed, out string? criticalError))
                {
                    messages.Add(criticalError!);
                    DisposeCurves(profiles);
                    return false;
                }

                flattened.Add(transformed.X, transformed.Y, profileElevation);
            }

            var profile = new PolylineCurve(flattened);
            if (!ValidateProfile(profile, outputTolerance, out string? profileError))
            {
                messages.Add($"Terrain profile is invalid: {profileError}");
                profile.Dispose();
                DisposeCurves(profiles);
                return false;
            }

            profiles.Add(profile);
        }

        if (!ValidateMutualProfileIntersections(profiles, outputTolerance, out string? outerIntersectionError))
        {
            messages.Add(outerIntersectionError!);
            DisposeCurves(profiles);
            return false;
        }
        if (!ValidateSingleProfileDomain(profiles, outputTolerance, out string? domainError))
        {
            messages.Add(domainError!);
            DisposeCurves(profiles);
            return false;
        }

        var transformedBreaklines = new List<Curve>(terrain.Breaklines.Count);
        foreach (Curve sourceBreakline in terrain.Breaklines)
        {
            Curve transformed = sourceBreakline.DuplicateCurve();
            if (!transformed.Transform(sourceToOutput) || !transformed.IsValid)
            {
                transformed.Dispose();
                messages.Add("Skipped an invalid breakline during coordinate conversion.");
                continue;
            }

            transformedBreaklines.Add(transformed);
            AddBreaklineSamples(terrain.Mesh, sourceBreakline, sourceToOutput, sourceTolerance, criticalPoints, messages);
        }

        var subdivisions = new List<ToposolidSubdivisionData>(terrain.Regions.Count);
        foreach (MoleHillTerrainRegion region in terrain.Regions)
        {
            var regionProfiles = new List<Curve>(region.Boundaries.Count);
            string? regionError = null;
            foreach (Curve sourceBoundary in region.Boundaries)
            {
                Curve flattened = sourceBoundary.DuplicateCurve();
                if (!flattened.Transform(sourceToOutput) ||
                    !flattened.Transform(Transform.PlanarProjection(Plane.WorldXY)))
                {
                    flattened.Dispose();
                    regionError ??= "coordinate conversion failed";
                    break;
                }

                Curve? polylineProfile = CreatePolylineProfile(flattened, outputTolerance, profileElevation);
                flattened.Dispose();
                if (polylineProfile == null || !ValidateProfile(polylineProfile, outputTolerance, out regionError))
                {
                    polylineProfile?.Dispose();
                    regionError ??= "could not create a valid polyline profile";
                    break;
                }

                regionProfiles.Add(polylineProfile);
            }

            if (regionError == null &&
                !ValidateMutualProfileIntersections(regionProfiles, outputTolerance, out regionError))
            {
                // The region is rejected atomically below.
            }
            if (regionError == null)
                ValidateSingleProfileDomain(regionProfiles, outputTolerance, out regionError);

            if (regionError != null)
            {
                DisposeCurves(regionProfiles);
                DisposeCurves(profiles);
                DisposeCurves(transformedBreaklines);
                messages.Add($"Subdivision '{region.Name}' is invalid: {regionError}");
                return false;
            }

            subdivisions.Add(new ToposolidSubdivisionData(
                region.Name,
                region.Key,
                regionProfiles,
                ComputeFingerprint(regionProfiles, Array.Empty<Point3d>(), outputTolerance, terrain.MetersPerModelUnit)));
            DisposeCurves(regionProfiles);
        }

        if (criticalPoints.Points.Count > options.MaximumPointCount)
        {
            DisposeCurves(profiles);
            DisposeCurves(transformedBreaklines);
            messages.Add(
                $"{criticalPoints.Points.Count:N0} boundary/breakline-critical points exceed the {options.MaximumPointCount:N0}-point budget. " +
                "Increase the budget or simplify the source constraints.");
            return false;
        }

        if (!ToposolidPointReducer.TryReduce(
                FlattenPoints(sourceVertices),
                sourceVertices.Length,
                FlattenPoints(criticalPoints.Points),
                criticalPoints.Points.Count,
                options.MaximumPointCount,
                verticalTolerance,
                outputTolerance,
                out ToposolidPointReducer.ReductionResult? reduction,
                out string? samplingError))
        {
            DisposeCurves(profiles);
            DisposeCurves(transformedBreaklines);
            messages.Add(samplingError ?? "Could not create the bounded elevation-point set.");
            return false;
        }
        IReadOnlyList<Point3d> elevationPoints = ExpandPoints(reduction!.Vertices);
        double maximumError = reduction.MaximumMeasuredVerticalError;

        messages.Add(
            $"Prepared {elevationPoints.Count:N0} of {sourceHeightfield.Points.Count:N0} unique source points " +
            $"with maximum measured vertex error {maximumError:G6} {terrain.UnitSystem}.");
        messages.Add("Coordinates were left unchanged; apply any project/shared-coordinate transform upstream before preparation.");
        if (transformedBreaklines.Count > 0)
        {
            messages.Add(
                "Breakline-critical points were retained, but Revit Toposolid creation does not guarantee the source breaklines as exact TIN edges.");
        }
        if (maximumError > verticalTolerance)
        {
            messages.Add(
                $"The {options.MaximumPointCount:N0}-point budget was reached before the requested vertical tolerance " +
                $"of {verticalTolerance:G6} {terrain.UnitSystem}; measured error is {maximumError:G6} {terrain.UnitSystem}.");
        }

        string fingerprint = ComputeFingerprint(profiles, elevationPoints, outputTolerance, terrain.MetersPerModelUnit);
        preparation = new ToposolidPreparationData(
            profiles,
            elevationPoints,
            subdivisions,
            transformedBreaklines,
            terrain.Name,
            terrain.Key,
            terrain.Revision,
            fingerprint,
            terrain.UnitSystem,
            terrain.MetersPerModelUnit,
            sourceHeightfield.Points.Count,
            maximumError,
            messages.Concat(terrain.Diagnostics));
        DisposeCurves(profiles);
        DisposeCurves(transformedBreaklines);
        return true;
    }

    private static bool ContainsNearVerticalFace(Mesh mesh, Transform transform, double tolerance)
    {
        for (int faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
        {
            MeshFace face = mesh.Faces[faceIndex];
            if (IsNearVertical(mesh, face.A, face.B, face.C, transform, tolerance))
                return true;
            if (face.IsQuad && IsNearVertical(mesh, face.A, face.C, face.D, transform, tolerance))
                return true;
        }

        return false;
    }

    private static bool IsNearVertical(Mesh mesh, int a, int b, int c, Transform transform, double tolerance)
    {
        Point3d p0 = TransformPoint(new Point3d(mesh.Vertices[a]), transform);
        Point3d p1 = TransformPoint(new Point3d(mesh.Vertices[b]), transform);
        Point3d p2 = TransformPoint(new Point3d(mesh.Vertices[c]), transform);
        Vector3d cross = Vector3d.CrossProduct(p1 - p0, p2 - p0);
        double length = cross.Length;
        return length > tolerance * tolerance && Math.Abs(cross.Z) / length < 1e-6;
    }

    private static bool ValidateProfile(Curve profile, double tolerance, out string? error)
    {
        error = null;
        if (!profile.IsValid || !profile.IsClosed)
        {
            error = "profile must be a valid closed curve";
            return false;
        }

        if (profile.TryGetPolyline(out Polyline polyline))
        {
            for (int index = 1; index < polyline.Count; index++)
            {
                if (polyline[index - 1].DistanceTo(polyline[index]) <= tolerance)
                {
                    error = "profile contains a zero-length or tolerance-short segment";
                    return false;
                }
            }
        }

        if (!profile.TryGetPlane(out Plane plane, tolerance) ||
            Math.Abs(Math.Abs(plane.Normal * Vector3d.ZAxis) - 1.0) > 1e-8)
        {
            error = "profile must be horizontal and planar";
            return false;
        }

        using AreaMassProperties? properties = AreaMassProperties.Compute(profile);
        if (properties == null || !double.IsFinite(properties.Area) || properties.Area <= tolerance * tolerance)
        {
            error = "profile has zero or invalid plan area";
            return false;
        }

        using var intersections = Intersection.CurveSelf(profile, tolerance);
        if (intersections != null && intersections.Count > 0)
        {
            error = "profile self-intersects";
            return false;
        }

        return true;
    }

    private static Curve? CreatePolylineProfile(Curve curve, double tolerance, double elevation)
    {
        if (!curve.TryGetPolyline(out Polyline polyline))
        {
            using PolylineCurve? approximation = curve.ToPolyline(
                tolerance,
                Math.PI / 180.0,
                tolerance,
                0.0);
            if (approximation == null || !approximation.TryGetPolyline(out polyline))
                return null;
        }

        var flattened = new Polyline(polyline.Count);
        foreach (Point3d point in polyline)
            flattened.Add(point.X, point.Y, elevation);
        if (!flattened.IsClosed && flattened.Count > 0)
            flattened.Add(flattened[0]);
        return new PolylineCurve(flattened);
    }

    private static bool ValidateMutualProfileIntersections(
        IReadOnlyList<Curve> profiles,
        double tolerance,
        out string? error)
    {
        error = null;
        for (int first = 0; first < profiles.Count; first++)
        {
            for (int second = first + 1; second < profiles.Count; second++)
            {
                using var intersections = Intersection.CurveCurve(
                    profiles[first],
                    profiles[second],
                    tolerance,
                    tolerance);
                if (intersections != null && intersections.Count > 0)
                {
                    error = $"profiles {first + 1} and {second + 1} intersect";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidateSingleProfileDomain(
        IReadOnlyList<Curve> profiles,
        double tolerance,
        out string? error)
    {
        error = null;
        if (profiles.Count == 0)
        {
            error = "A Toposolid profile collection cannot be empty.";
            return false;
        }

        var containmentDepth = new int[profiles.Count];
        for (int first = 0; first < profiles.Count; first++)
        {
            for (int second = first + 1; second < profiles.Count; second++)
            {
                RegionContainment relationship = Curve.PlanarClosedCurveRelationship(
                    profiles[first],
                    profiles[second],
                    Plane.WorldXY,
                    tolerance);
                if (relationship == RegionContainment.AInsideB)
                    containmentDepth[first]++;
                else if (relationship == RegionContainment.BInsideA)
                    containmentDepth[second]++;
            }
        }

        if (containmentDepth.Count(depth => depth == 0) != 1)
        {
            error = "Profiles contain multiple disjoint outer domains. Partition them into one terrain branch per independent Toposolid.";
            return false;
        }

        if (containmentDepth.Any(depth => depth > 1))
        {
            error = "Profiles contain nested islands deeper than one outer boundary plus holes; split them into independent Toposolids.";
            return false;
        }

        return true;
    }

    private static void AddBreaklineSamples(
        Mesh sourceMesh,
        Curve sourceBreakline,
        Transform sourceToOutput,
        double sourceTolerance,
        PointAccumulator criticalPoints,
        ICollection<string> messages)
    {
        var samples = new List<Point3d>();
        if (sourceBreakline.TryGetPolyline(out Polyline polyline))
        {
            samples.AddRange(polyline);
        }
        else
        {
            double length = sourceBreakline.GetLength();
            int count = Math.Clamp((int)Math.Ceiling(length / Math.Max(sourceTolerance * 25.0, length / 256.0)), 1, 256);
            double[] parameters = sourceBreakline.DivideByCount(count, includeEnds: true) ??
                                  new[] { sourceBreakline.Domain.T0, sourceBreakline.Domain.T1 };
            samples.AddRange(parameters.Select(sourceBreakline.PointAt));
        }

        int skipped = 0;
        foreach (Point3d point in samples)
        {
            if (sourceMesh.ClosestPoint(point, out Point3d onMesh, sourceTolerance * 10.0) < 0)
            {
                skipped++;
                continue;
            }

            if (!criticalPoints.TryAdd(TransformPoint(onMesh, sourceToOutput), out _))
                skipped++;
        }

        if (skipped > 0)
            messages.Add($"Skipped {skipped:N0} breakline samples that were off the terrain or height-conflicting.");
    }

    private static Point3d TransformPoint(Point3d point, Transform transform)
    {
        point.Transform(transform);
        return point;
    }

    private static double[] FlattenPoints(IReadOnlyList<Point3d> points)
    {
        var result = new double[points.Count * 3];
        for (int index = 0; index < points.Count; index++)
        {
            result[index * 3] = points[index].X;
            result[index * 3 + 1] = points[index].Y;
            result[index * 3 + 2] = points[index].Z;
        }

        return result;
    }

    private static IReadOnlyList<Point3d> ExpandPoints(double[] values)
    {
        var result = new Point3d[values.Length / 3];
        for (int index = 0; index < result.Length; index++)
            result[index] = new Point3d(values[index * 3], values[index * 3 + 1], values[index * 3 + 2]);
        return result;
    }

    private static string ComputeFingerprint(
        IReadOnlyList<Curve> profiles,
        IReadOnlyList<Point3d> points,
        double tolerance,
        double metersPerModelUnit)
    {
        double quantum = Math.Max(tolerance, 1e-9);
        var lines = new List<string>(points.Count + profiles.Count);
        lines.Add($"U:{metersPerModelUnit.ToString("R", CultureInfo.InvariantCulture)}");
        foreach (Point3d point in points)
            lines.Add($"P:{Quantize(point.X, quantum)}:{Quantize(point.Y, quantum)}:{Quantize(point.Z, quantum)}");

        foreach (Curve profile in profiles)
        {
            var samples = new List<Point3d>();
            if (profile.TryGetPolyline(out Polyline polyline))
                samples.AddRange(polyline);
            else
            {
                double[] parameters = profile.DivideByCount(64, includeEnds: true) ??
                                      new[] { profile.Domain.T0, profile.Domain.T1 };
                samples.AddRange(parameters.Select(profile.PointAt));
            }

            string canonical = CanonicalLoop(samples, quantum);
            lines.Add($"C:{canonical}");
        }

        lines.Sort(StringComparer.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Quantize(double value, double quantum) =>
        Math.Round(value / quantum, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);

    private static string CanonicalLoop(IReadOnlyList<Point3d> samples, double quantum)
    {
        var tokens = samples
            .Select(point => $"{Quantize(point.X, quantum)}:{Quantize(point.Y, quantum)}")
            .ToList();
        if (tokens.Count > 1 && tokens[0] == tokens[^1])
            tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0)
            return string.Empty;

        string minimum = tokens.Min(StringComparer.Ordinal)!;
        string? best = null;
        for (int start = 0; start < tokens.Count; start++)
        {
            if (!string.Equals(tokens[start], minimum, StringComparison.Ordinal))
                continue;
            string forward = string.Join(",", Enumerable.Range(0, tokens.Count)
                .Select(offset => tokens[(start + offset) % tokens.Count]));
            string reverse = string.Join(",", Enumerable.Range(0, tokens.Count)
                .Select(offset => tokens[(start - offset + tokens.Count) % tokens.Count]));
            string candidate = string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse;
            if (best == null || string.CompareOrdinal(candidate, best) < 0)
                best = candidate;
        }

        return best ?? string.Join(",", tokens);
    }

    private static void DisposeCurves(IEnumerable<Curve> curves)
    {
        foreach (Curve curve in curves)
            curve.Dispose();
    }
}
