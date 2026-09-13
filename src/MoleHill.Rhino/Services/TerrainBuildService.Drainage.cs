using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Identity of a basin graph within one analysis pass. The mesh is fixed for the pass, so the routing
/// settings are the whole key — two drainage cards that agree on them share one graph instead of routing
/// the terrain twice, which is the entire reason the two analyses have a common base.
/// </summary>
internal readonly record struct BasinGraphCacheKey(double FlatSlopeRatio, double MergeShare);

internal sealed partial class TerrainBuildService
{
    private static TerrainAnalysisSummary BuildCatchmentSummary(
        TerrainBuildSnapshot snapshot,
        double[] vertices,
        int[] faces,
        CatchmentAnalysisDefinition analysis,
        TerrainBuildResult build,
        Dictionary<BasinGraphCacheKey, BasinGraph> basinGraphCache,
        Func<bool>? shouldCancel)
    {
        // Counts come from the extracted arrays, never from the Rhino mesh they were taken from: the
        // extraction normalises a copy, so the two routinely disagree. See the note in CLAUDE.md.
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;

        BasinGraph graph = ResolveBasinGraph(
            vertices, vertexCount, faces, faceCount, analysis, basinGraphCache, shouldCancel);

        int outputCount = 0;
        if (analysis.ShowBoundaries)
        {
            outputCount += DrawCatchmentBoundaries(
                snapshot, graph, vertices, vertexCount, faces, analysis, build, shouldCancel);
        }

        if (analysis.ShowFlowPaths)
        {
            outputCount += DrawCatchmentFlowPaths(
                snapshot, graph, vertices, vertexCount, faces, faceCount, analysis, build, shouldCancel);
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            CatchmentBasinCount = graph.Basins.Count,
            CatchmentSinkCount = graph.SinkBasinCount,
            CatchmentLargestArea = graph.Basins.Count == 0 ? null : graph.Basins[0].PlanArea,
            CatchmentFlatFaceCount = graph.FlatFaceCount,
            GeneratedOutputCount = outputCount
        };
    }

    private static BasinGraph ResolveBasinGraph(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        DrainageAnalysisDefinition analysis,
        Dictionary<BasinGraphCacheKey, BasinGraph> basinGraphCache,
        Func<bool>? shouldCancel)
    {
        double flatSlopeRatio = SlopeAnalyzer.ConvertUnitToRatio(
            analysis.FlatSlopeThresholdDegrees, SlopeAnalyzer.SlopeUnit.Degrees);
        double mergeShare = analysis is CatchmentAnalysisDefinition catchment
            ? Math.Clamp(catchment.MinimumBasinAreaPercent, 0.0, 100.0) / 100.0
            : 0.0;

        var key = new BasinGraphCacheKey(flatSlopeRatio, mergeShare);
        if (basinGraphCache.TryGetValue(key, out BasinGraph? cached))
            return cached;

        BasinGraph graph = DrainageBasinAnalyzer.Analyze(
            vertices,
            vertexCount,
            faces,
            faceCount,
            new DrainageBasinAnalyzer.Options
            {
                FlatSlopeRatio = flatSlopeRatio,
                MinimumBasinAreaShare = mergeShare,
                CancellationRequested = shouldCancel
            });

        basinGraphCache[key] = graph;
        return graph;
    }

    private static int DrawCatchmentBoundaries(
        TerrainBuildSnapshot snapshot,
        BasinGraph graph,
        double[] vertices,
        int vertexCount,
        int[] faces,
        CatchmentAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        string layerPath = snapshot.LayerRoles.Path(LayerRole.Catchments);
        int drawn = 0;

        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            ThrowIfCancellationRequested(shouldCancel);
            List<double[]> loops = BasinBoundaryExtractor.Extract(
                graph, vertices, vertexCount, faces, basin.Index);

            foreach (double[] loop in loops)
            {
                int pointCount = loop.Length / 3;
                if (pointCount < 4)
                    continue;

                var polyline = new Polyline(pointCount);
                for (int index = 0; index < pointCount; index++)
                {
                    polyline.Add(new Point3d(
                        loop[index * 3],
                        loop[(index * 3) + 1],
                        loop[(index * 3) + 2]));
                }

                drawn++;
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Role = LayerRole.Catchments,
                    Geometry = new PolylineCurve(polyline),
                    Name = $"{analysis.Label} {basin.Index + 1}",
                    AnalysisId = analysis.Id,
                    ColorArgb = analysis.BoundaryColorArgb,
                    LayerPath = layerPath
                });
            }
        }

        return drawn;
    }

    /// <summary>
    /// One path per catchment, traced downhill from the head <see cref="BasinGraph.Basin.FlowStartX"/>
    /// names — the centroid of the basin's highest falling face, for the reasons recorded there.
    ///
    /// Traced rather than read off the basin's own face pointers: <see cref="WaterflowTracer"/> follows
    /// the gradient continuously within each face, so its line lands where water lands, while the face
    /// pointers only say which face is next and would draw a staircase between centroids.
    /// </summary>
    private static int DrawCatchmentFlowPaths(
        TerrainBuildSnapshot snapshot,
        BasinGraph graph,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        CatchmentAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        if (graph.Basins.Count == 0)
            return 0;

        var starts = new double[graph.Basins.Count * 2];
        for (int index = 0; index < graph.Basins.Count; index++)
        {
            starts[index * 2] = graph.Basins[index].FlowStartX;
            starts[(index * 2) + 1] = graph.Basins[index].FlowStartY;
        }

        WaterflowTracer.Result traced = WaterflowTracer.Trace(
            vertices,
            vertexCount,
            faces,
            faceCount,
            starts,
            graph.Basins.Count,
            new WaterflowTracer.Options
            {
                Tolerance = Math.Max(snapshot.ModelAbsoluteTolerance * 1e-3, 1e-10),
                CancellationRequested = shouldCancel
            });

        string layerPath = snapshot.LayerRoles.Path(LayerRole.CatchmentFlowPaths);
        int drawn = 0;
        foreach (WaterflowTracer.Path path in traced.Paths)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (path.PointCount < 2)
                continue;

            var polyline = new Polyline(path.PointCount);
            for (int index = 0; index < path.PointCount; index++)
            {
                polyline.Add(new Point3d(
                    path.PointsXyz[index * 3],
                    path.PointsXyz[(index * 3) + 1],
                    path.PointsXyz[(index * 3) + 2]));
            }

            drawn++;
            build.AuxiliaryObjects.Add(new GeneratedRhinoObject
            {
                Role = LayerRole.CatchmentFlowPaths,
                Geometry = new PolylineCurve(polyline),
                Name = $"{analysis.Label} flow {drawn}",
                AnalysisId = analysis.Id,
                ColorArgb = analysis.FlowPathColorArgb,
                LayerPath = layerPath
            });
        }

        return drawn;
    }

    /// <summary>
    /// Per-face colours for the catchment preview. Categorical, not ramped: a basin index is a name, not
    /// a magnitude, so what the colouring owes the reader is that two adjacent catchments never look
    /// alike — the opposite of the "near values, near colours" a ramp provides.
    /// </summary>
    internal static byte[] BuildCatchmentFaceColors(BasinGraph graph)
    {
        var colors = new byte[graph.FaceCount * 3];
        for (int face = 0; face < graph.FaceCount; face++)
        {
            (byte r, byte g, byte b) = CategoricalPalette.ColorAt(graph.FaceBasin[face]);
            colors[face * 3] = r;
            colors[(face * 3) + 1] = g;
            colors[(face * 3) + 2] = b;
        }

        return colors;
    }
}
