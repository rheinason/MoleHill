using System.Diagnostics;
using System.Globalization;
using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Forensic harness: runs the isotropic remesher on the real graded mesh exported from the
/// GradePadTest scene (scratch_graded_input.obj at the repo root). Skips when the export is absent.
/// </summary>
public class IsotropicRemesherForensicTests
{
    private readonly ITestOutputHelper _output;

    public IsotropicRemesherForensicTests(ITestOutputHelper output) => _output = output;

    private static string? FindObj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "scratch_graded_input.obj");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static (double[] vertices, int[] faces) LoadObj(string path)
    {
        var verts = new List<double>();
        var faces = new List<int>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("v "))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                verts.Add(double.Parse(p[1], CultureInfo.InvariantCulture));
                verts.Add(double.Parse(p[2], CultureInfo.InvariantCulture));
                verts.Add(double.Parse(p[3], CultureInfo.InvariantCulture));
            }
            else if (line.StartsWith("f "))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                faces.Add(int.Parse(p[1], CultureInfo.InvariantCulture) - 1);
                faces.Add(int.Parse(p[2], CultureInfo.InvariantCulture) - 1);
                faces.Add(int.Parse(p[3], CultureInfo.InvariantCulture) - 1);
            }
        }

        return (verts.ToArray(), faces.ToArray());
    }

    [Fact]
    public void Remesh_RealGradedScene_ProducesSaneFaceCount()
    {
        string? path = FindObj();
        if (path == null)
        {
            _output.WriteLine("scratch_graded_input.obj not found; skipping.");
            return;
        }

        var (vertices, faces) = LoadObj(path);
        _output.WriteLine($"input: {vertices.Length / 3} verts / {faces.Length / 3} faces");

        var sw = Stopwatch.StartNew();
        var result = IsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new IsotropicRemesher.Options { TargetEdgeLength = 3.0, CreaseAngleDeg = 30, Tolerance = 0.01 });
        sw.Stop();

        _output.WriteLine($"output: {result.Vertices.Length / 3} verts / {result.Faces.Length / 3} faces; success={result.Success}");
        _output.WriteLine($"splits={result.Splits} collapses={result.Collapses} flips={result.Flips} relaxed={result.RelaxedVertices}");
        _output.WriteLine($"timing: {result.Timing}; total {sw.ElapsedMilliseconds} ms");
        if (result.Warning != null)
            _output.WriteLine("warning: " + result.Warning);

        // Edge length histogram of the output.
        var seen = new HashSet<long>();
        var buckets = new int[8]; // <0.5, 0.5-1, 1-2, 2-3, 3-4, 4-6, 6-10, >10
        for (int t = 0; t < result.Faces.Length / 3; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                int a = result.Faces[t * 3 + c];
                int b = result.Faces[t * 3 + ((c + 1) % 3)];
                if (!seen.Add(IndexedMeshTools.GetEdgeKey(a, b)))
                    continue;
                double dx = result.Vertices[a * 3] - result.Vertices[b * 3];
                double dy = result.Vertices[a * 3 + 1] - result.Vertices[b * 3 + 1];
                double dz = result.Vertices[a * 3 + 2] - result.Vertices[b * 3 + 2];
                double len = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                int bucket = len < 0.5 ? 0 : len < 1 ? 1 : len < 2 ? 2 : len < 3 ? 3 : len < 4 ? 4 : len < 6 ? 5 : len < 10 ? 6 : 7;
                buckets[bucket]++;
            }
        }

        _output.WriteLine($"edge histogram: <0.5:{buckets[0]} 0.5-1:{buckets[1]} 1-2:{buckets[2]} 2-3:{buckets[3]} 3-4:{buckets[4]} 4-6:{buckets[5]} 6-10:{buckets[6]} >10:{buckets[7]}");
    }

    [Fact]
    public void QuadRemesh_RealGradedScene_ThresholdSweep()
    {
        string? path = FindObj();
        if (path == null)
        {
            _output.WriteLine("scratch_graded_input.obj not found; skipping.");
            return;
        }

        var (vertices, faces) = LoadObj(path);
        foreach (double threshold in new[] { 0.8, 1.0, 1.2, 1.5 })
        {
            var field = MoleHill.Core.Retopo.CrossFieldSolver.Solve(vertices, faces,
                Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                new MoleHill.Core.Retopo.CrossFieldSolver.Options { CreaseAngleDeg = 30, Tolerance = 0.01 });
            var remesh = IsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                new IsotropicRemesher.Options { TargetEdgeLength = 3.0, CreaseAngleDeg = 30, Tolerance = 0.01, FieldTheta = field.Theta });
            Assert.True(remesh.Success, remesh.Warning);

            var featureEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
            for (int i = 0; i < remesh.FeatureEdges.Length; i += 2)
                featureEdges.Add(IndexedMeshTools.GetEdgeKey(remesh.FeatureEdges[i], remesh.FeatureEdges[i + 1]));
            var sampler = new IsotropicRemesher.FieldSampler(vertices, faces, field.Theta,
                new MoleHill.Core.Grading.TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3, 1.5));

            var paired = MoleHill.Core.Retopo.TriQuadPairer.Pair(remesh.Vertices, remesh.Faces, featureEdges, remesh.FrozenFaces,
                new MoleHill.Core.Retopo.TriQuadPairer.Options
                {
                    ThetaSampler = (x, y) => sampler.SampleTheta(x, y, double.NaN),
                    AcceptThreshold = threshold
                });

            int pairedTris = paired.QuadCount * 2;
            int total = remesh.Faces.Length / 3;
            _output.WriteLine($"threshold {threshold:0.0}: {paired.QuadCount} quads, {paired.Tris.Length / 3} tris " +
                              $"({100.0 * pairedTris / total:0}% of triangles paired)");
        }
    }

    [Fact]
    public void Remesh_RealGradedScene_PhaseByPhaseTopology()
    {
        string? path = FindObj();
        if (path == null)
        {
            _output.WriteLine("scratch_graded_input.obj not found; skipping.");
            return;
        }

        var (vertices, faces) = LoadObj(path);
        const double target = 3.0;
        var graph = FeaturePolylineGraph.Build(vertices, faces, faces.Length / 3,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 30.0, 0.0, 0.01, minCreaseChainLength: target * 3.0);
        var projection = new MoleHill.Core.Grading.TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3, target * 0.5);
        var state = new IsotropicRemesher.MeshState(vertices, faces, graph);

        void Report(string label)
        {
            var live = new List<int>();
            for (int t = 0; t < state.FaceCount; t++)
            {
                if (state.Tris[t * 3] < 0)
                    continue;
                live.Add(state.Tris[t * 3]);
                live.Add(state.Tris[t * 3 + 1]);
                live.Add(state.Tris[t * 3 + 2]);
            }

            var topo = MeshTopologyValidator.AnalyzeBoundaryGraph(live.ToArray(), live.Count / 3);
            _output.WriteLine($"{label}: faces={live.Count / 3} verts~{state.VertexCount} nonManifold={topo.NonManifoldEdgeCount} openChains={topo.HasOpenBoundaryChains}");
        }

        int free = 0, feature = 0, corner = 0, frozen = 0;
        foreach (byte k in graph.VertexKind)
        {
            if (k == FeaturePolylineGraph.KindFree) free++;
            else if (k == FeaturePolylineGraph.KindFeature) feature++;
            else if (k == FeaturePolylineGraph.KindCorner) corner++;
            else frozen++;
        }

        _output.WriteLine($"classification: free={free} feature={feature} corner={corner} frozen={frozen}; " +
                          $"featureEdges={graph.FeatureEdgeChains.Count} chains={graph.Chains.Count} " +
                          $"frozenFaces={graph.FrozenFaces.Count(x => x)}");

        Report("input");
        for (int iteration = 0; iteration < 3; iteration++)
        {
            int collapses = IsotropicRemesher.CollapseShortEdges(state, target, projection);
            Report($"iter{iteration} after collapse ({collapses})");
            int splits = IsotropicRemesher.SplitLongEdges(state, target * 4.0 / 3.0, projection);
            Report($"iter{iteration} after split ({splits})");
            int flips = IsotropicRemesher.FlipForQuality(state);
            Report($"iter{iteration} after flip ({flips})");
            int relaxed = IsotropicRemesher.RelaxAndProject(state, target, projection);
            Report($"iter{iteration} after relax ({relaxed})");
        }
    }
}
