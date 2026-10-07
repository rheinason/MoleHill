using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>Conforms and applies terrain-wide Outer, Hide, and Show World-XY regions.</summary>
public static class TerrainBoundaryTrimmer
{
    public sealed class Result
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
        public bool ShowRestoredAnyFace { get; init; }
    }

    public static Result? Trim(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        MeshAreaSplitter.AreaBoundary? outer,
        IReadOnlyList<MeshAreaSplitter.AreaBoundary> hides,
        IReadOnlyList<MeshAreaSplitter.AreaBoundary> shows,
        double tolerance,
        out string? errorMessage,
        Func<bool>? shouldCancel = null)
    {
        errorMessage = null;
        var all = new List<MeshAreaSplitter.AreaBoundary>();
        if (outer != null)
            all.Add(outer);
        all.AddRange(hides);
        all.AddRange(shows);
        if (all.Count == 0)
        {
            return new Result
            {
                Vertices = (double[])vertices.Clone(),
                VertexCount = vertexCount,
                Faces = (int[])faces.Clone(),
                FaceCount = faceCount
            };
        }

        MeshAreaSplitter.SplitResult? split = MeshAreaSplitter.SplitPreservingTopology(
            new IndexedTriMesh(vertices, vertexCount, faces, faceCount), all.ToArray(), tolerance, out errorMessage, shouldCancel);
        if (split == null)
            return null;

        bool[] insideOuter = ClassifyUnion(split, outer == null ? Array.Empty<MeshAreaSplitter.AreaBoundary>() : new[] { outer }, tolerance, defaultWhenEmpty: true);
        bool[] insideHide = ClassifyUnion(split, hides, tolerance, defaultWhenEmpty: false);
        bool[] insideShow = ClassifyUnion(split, shows, tolerance, defaultWhenEmpty: false);
        var keep = new bool[split.FaceCount];
        bool showRestored = false;
        int keptCount = 0;
        for (int i = 0; i < split.FaceCount; i++)
        {
            keep[i] = insideOuter[i] && (!insideHide[i] || insideShow[i]);
            if (keep[i])
                keptCount++;
            if (insideOuter[i] && insideHide[i] && insideShow[i])
                showRestored = true;
        }

        if (keptCount == 0)
        {
            return new Result
            {
                Vertices = Array.Empty<double>(),
                VertexCount = 0,
                Faces = Array.Empty<int>(),
                FaceCount = 0,
                ShowRestoredAnyFace = showRestored
            };
        }

        Result compact = Compact(split, keep, keptCount, showRestored);
        MeshTopologyValidator.BoundaryGraphAnalysis topology = MeshTopologyValidator.AnalyzeBoundaryGraph(compact.Faces, compact.FaceCount);
        if (topology.NonManifoldEdgeCount > 0 || topology.HasOpenBoundaryChains)
        {
            errorMessage = $"Boundary trim produced invalid topology ({topology.NonManifoldEdgeCount} non-manifold edges, open chains: {topology.HasOpenBoundaryChains}).";
            return null;
        }

        return compact;
    }

    private static bool[] ClassifyUnion(
        MeshAreaSplitter.SplitResult split,
        IReadOnlyList<MeshAreaSplitter.AreaBoundary> areas,
        double tolerance,
        bool defaultWhenEmpty)
    {
        if (areas.Count == 0)
        {
            var defaults = new bool[split.FaceCount];
            if (defaultWhenEmpty)
                Array.Fill(defaults, true);
            return defaults;
        }

        // The split already conformed the mesh to every boundary, so each face lies wholly on one side: own it
        // strictly, as the splitter does. Classifying with the boundary tolerance treated outside faces whose
        // centroid came within it as inside - on RiR Master 002, ten whole triangles beyond a Hide curve,
        // which broke the trimmed border into open chains, so the trim was rejected and Hide did nothing.
        MeshAreaSplitter.SplitResult classified = MeshAreaSplitter.Classify(
            new IndexedTriMesh(split.Vertices, split.VertexCount, split.Faces, split.FaceCount),
            areas.ToArray(), 0.0, out _)!;
        var result = new bool[split.FaceCount];
        for (int i = 0; i < result.Length; i++)
            result[i] = classified.FaceAreaIndex[i] >= 0;
        return result;
    }

    private static Result Compact(MeshAreaSplitter.SplitResult split, bool[] keep, int keptCount, bool showRestored)
    {
        var remap = new Dictionary<int, int>();
        var vertices = new List<double>();
        var faces = new int[keptCount * 3];
        int output = 0;
        for (int faceIndex = 0; faceIndex < split.FaceCount; faceIndex++)
        {
            if (!keep[faceIndex])
                continue;
            for (int corner = 0; corner < 3; corner++)
            {
                int source = split.Faces[(faceIndex * 3) + corner];
                if (!remap.TryGetValue(source, out int target))
                {
                    target = remap.Count;
                    remap[source] = target;
                    vertices.Add(split.Vertices[source * 3]);
                    vertices.Add(split.Vertices[(source * 3) + 1]);
                    vertices.Add(split.Vertices[(source * 3) + 2]);
                }
                faces[output++] = target;
            }
        }

        return new Result
        {
            Vertices = vertices.ToArray(),
            VertexCount = remap.Count,
            Faces = faces,
            FaceCount = keptCount,
            ShowRestoredAnyFace = showRestored
        };
    }
}
