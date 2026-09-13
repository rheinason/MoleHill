namespace MoleHill.Core.Analysis;

/// <summary>
/// Where every face's water goes: a downstream pointer per face, the basins those pointers resolve to,
/// and what each basin drains into.
/// </summary>
/// <remarks>
/// One result serves both drainage questions, which is why it is its own type rather than the return of
/// either analyzer. A catchment is a basin; a depression is a basin whose outlet is
/// <see cref="OutletKind.Sink"/> rather than the terrain edge. Computing this twice for a terrain
/// carrying both cards would be the single most expensive mistake available here, so the result is
/// shaped to be shared.
/// </remarks>
public sealed class BasinGraph
{
    /// <summary>What a basin ultimately drains into.</summary>
    public enum OutletKind
    {
        /// <summary>Water leaves the terrain across a naked edge. The normal case.</summary>
        Boundary,

        /// <summary>Water has nowhere lower to go and stays. A closed depression.</summary>
        Sink
    }

    public sealed class Basin
    {
        /// <summary>Position in <see cref="Basins"/>, and the value written into <see cref="FaceBasin"/>.</summary>
        public required int Index { get; init; }

        /// <summary>The face every pointer in this basin resolves to.</summary>
        public required int OutletFace { get; init; }

        public required OutletKind Outlet { get; init; }

        public required int FaceCount { get; init; }

        /// <summary>Plan (XY-projected) area. Vertical faces contribute nothing, which is what they cover.</summary>
        public required double PlanArea { get; init; }

        /// <summary>Lowest vertex elevation anywhere in the basin — the pond floor, for a sink.</summary>
        public required double LowestZ { get; init; }

        public required double LowestX { get; init; }

        public required double LowestY { get; init; }
    }

    /// <summary>Basin index per face, or -1 for a face that could not be routed (degenerate indices).</summary>
    public required int[] FaceBasin { get; init; }

    /// <summary>
    /// Downstream face per face, or -1 at a basin outlet. Following these from any face reaches that
    /// face's outlet in finitely many steps: cycles are broken during resolution, not left to a caller.
    /// </summary>
    public required int[] FlowsTo { get; init; }

    public required int FaceCount { get; init; }

    /// <summary>
    /// Face-to-face adjacency as <c>faceCount * 3</c> entries, -1 where an edge is naked. Carried on the
    /// result because every consumer needs it — boundary extraction walks it, and a ponding rim walk
    /// walks it again — and rebuilding it per consumer would cost more than the routing did.
    /// </summary>
    public required int[] Neighbors { get; init; }

    /// <summary>Basins, ordered by descending plan area so the large ones keep their index — and so a
    /// categorical colouring keeps its colours — when a small basin appears or merges away.</summary>
    public required IReadOnlyList<Basin> Basins { get; init; }

    /// <summary>Faces whose gradient fell below the flat threshold and were routed as flat ground.</summary>
    public required int FlatFaceCount { get; init; }

    /// <summary>Basins with no outlet. The count a ponding check reports on.</summary>
    public int SinkBasinCount
    {
        get
        {
            int count = 0;
            foreach (Basin basin in Basins)
            {
                if (basin.Outlet == OutletKind.Sink)
                    count++;
            }

            return count;
        }
    }

    /// <summary>Total routed plan area, so a basin's area can be read as a share of the terrain.</summary>
    public required double TotalPlanArea { get; init; }
}
