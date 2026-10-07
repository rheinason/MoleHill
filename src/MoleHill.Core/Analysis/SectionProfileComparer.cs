// Compares proposed/reference section profiles, splitting cut and fill at crossings and coverage gaps.
namespace MoleHill.Core.Analysis;

/// <summary>One sample of a section profile: distance along the cut line and the ground elevation there.</summary>
internal readonly record struct SectionProfilePoint(double Station, double Elevation);

internal readonly record struct SectionComparisonVertex(
    double Station,
    double ProposedElevation,
    double ReferenceElevation);

internal sealed class SectionComparisonRegion
{
    public SectionComparisonRegion(bool isCut, IReadOnlyList<SectionComparisonVertex> vertices)
    {
        IsCut = isCut;
        Vertices = vertices;
    }

    public bool IsCut { get; }

    public IReadOnlyList<SectionComparisonVertex> Vertices { get; }
}

internal static class SectionProfileComparer
{
    private readonly record struct ProfileEdge(
        double StartStation,
        double EndStation,
        double StartElevation,
        double EndElevation)
    {
        public double ElevationAt(double station)
        {
            double length = EndStation - StartStation;
            if (length <= double.Epsilon)
                return StartElevation;
            double t = Math.Clamp((station - StartStation) / length, 0.0, 1.0);
            return StartElevation + ((EndElevation - StartElevation) * t);
        }
    }

    private readonly record struct ComparisonPortion(
        bool IsCut,
        SectionComparisonVertex Start,
        SectionComparisonVertex End);

    public static IReadOnlyList<SectionComparisonRegion> Compare(
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> proposed,
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> reference,
        double tolerance)
    {
        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        List<ProfileEdge> proposedEdges = BuildEdges(proposed, resolvedTolerance);
        List<ProfileEdge> referenceEdges = BuildEdges(reference, resolvedTolerance);
        var portions = new List<ComparisonPortion>();

        int proposedIndex = 0;
        int referenceIndex = 0;
        while (proposedIndex < proposedEdges.Count && referenceIndex < referenceEdges.Count)
        {
            ProfileEdge p = proposedEdges[proposedIndex];
            ProfileEdge r = referenceEdges[referenceIndex];
            double start = Math.Max(p.StartStation, r.StartStation);
            double end = Math.Min(p.EndStation, r.EndStation);
            if (end - start > resolvedTolerance)
                AddPortions(portions, p, r, start, end, resolvedTolerance);

            if (p.EndStation < r.EndStation - resolvedTolerance)
                proposedIndex++;
            else if (r.EndStation < p.EndStation - resolvedTolerance)
                referenceIndex++;
            else
            {
                proposedIndex++;
                referenceIndex++;
            }
        }

        return MergePortions(portions, resolvedTolerance);
    }

    /// <summary>
    /// Flattens a sliced profile into station-ordered edges.
    ///
    /// A segment's vertices are not guaranteed to run in increasing station: the slicer walks mesh
    /// adjacency, so a run can come back descending. Each edge is therefore normalized to
    /// low-station-first. Assuming ascending order silently dropped every edge of a descending run — and a
    /// fully descending profile produced no edges at all, which is why cut/fill shading could come back
    /// empty on section lines that were otherwise perfectly valid.
    /// </summary>
    private static List<ProfileEdge> BuildEdges(IReadOnlyList<IReadOnlyList<SectionProfilePoint>> segments, double tolerance)
    {
        var edges = new List<ProfileEdge>();
        foreach (IReadOnlyList<SectionProfilePoint> segment in segments)
        {
            for (int i = 1; i < segment.Count; i++)
            {
                SectionProfilePoint a = segment[i - 1];
                SectionProfilePoint b = segment[i];
                if (Math.Abs(b.Station - a.Station) <= tolerance)
                    continue;

                edges.Add(a.Station <= b.Station
                    ? new ProfileEdge(a.Station, b.Station, a.Elevation, b.Elevation)
                    : new ProfileEdge(b.Station, a.Station, b.Elevation, a.Elevation));
            }
        }

        edges.Sort(static (a, b) => a.StartStation.CompareTo(b.StartStation));
        return edges;
    }

    private static void AddPortions(
        List<ComparisonPortion> portions,
        ProfileEdge proposed,
        ProfileEdge reference,
        double start,
        double end,
        double tolerance)
    {
        SectionComparisonVertex a = CreateVertex(proposed, reference, start);
        SectionComparisonVertex b = CreateVertex(proposed, reference, end);
        double deltaA = a.ProposedElevation - a.ReferenceElevation;
        double deltaB = b.ProposedElevation - b.ReferenceElevation;
        if (Math.Abs(deltaA) <= tolerance && Math.Abs(deltaB) <= tolerance)
            return;

        if (deltaA * deltaB < -(tolerance * tolerance))
        {
            double t = deltaA / (deltaA - deltaB);
            double station = start + ((end - start) * t);
            SectionComparisonVertex crossing = CreateVertex(proposed, reference, station);
            AddNonZeroPortion(portions, a, crossing, tolerance);
            AddNonZeroPortion(portions, crossing, b, tolerance);
            return;
        }

        AddNonZeroPortion(portions, a, b, tolerance);
    }

    private static SectionComparisonVertex CreateVertex(ProfileEdge proposed, ProfileEdge reference, double station) =>
        new(station, proposed.ElevationAt(station), reference.ElevationAt(station));

    private static void AddNonZeroPortion(
        List<ComparisonPortion> portions,
        SectionComparisonVertex start,
        SectionComparisonVertex end,
        double tolerance)
    {
        if (end.Station - start.Station <= tolerance)
            return;
        double midpointDelta =
            ((start.ProposedElevation + end.ProposedElevation) * 0.5) -
            ((start.ReferenceElevation + end.ReferenceElevation) * 0.5);
        if (Math.Abs(midpointDelta) <= tolerance)
            return;
        portions.Add(new ComparisonPortion(midpointDelta < 0.0, start, end));
    }

    private static IReadOnlyList<SectionComparisonRegion> MergePortions(
        IReadOnlyList<ComparisonPortion> portions,
        double tolerance)
    {
        var regions = new List<SectionComparisonRegion>();
        List<SectionComparisonVertex>? current = null;
        bool currentIsCut = false;

        foreach (ComparisonPortion portion in portions)
        {
            bool joins = current is { Count: > 0 } &&
                         currentIsCut == portion.IsCut &&
                         AreCoincident(current[^1], portion.Start, tolerance);
            if (!joins)
            {
                Flush(regions, currentIsCut, current);
                current = new List<SectionComparisonVertex> { portion.Start, portion.End };
                currentIsCut = portion.IsCut;
            }
            else
            {
                current!.Add(portion.End);
            }
        }

        Flush(regions, currentIsCut, current);
        return regions;
    }

    private static bool AreCoincident(SectionComparisonVertex a, SectionComparisonVertex b, double tolerance) =>
        Math.Abs(a.Station - b.Station) <= tolerance &&
        Math.Abs(a.ProposedElevation - b.ProposedElevation) <= tolerance &&
        Math.Abs(a.ReferenceElevation - b.ReferenceElevation) <= tolerance;

    private static void Flush(
        List<SectionComparisonRegion> regions,
        bool isCut,
        List<SectionComparisonVertex>? vertices)
    {
        if (vertices is { Count: >= 2 })
            regions.Add(new SectionComparisonRegion(isCut, vertices.ToArray()));
    }
}
