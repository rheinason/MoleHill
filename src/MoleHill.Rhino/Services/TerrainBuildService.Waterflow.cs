using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    private static TerrainAnalysisSummary BuildWaterflowSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        double[] vertices,
        int[] faces,
        WaterflowAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath)
    {
        var sourcePoints = TerrainBuildSnapshotResolver.ResolvePoints(snapshot, analysis.Sources);
        if (sourcePoints.Count == 0)
        {
            return new TerrainAnalysisSummary
            {
                AnalysisId = analysis.Id,
                WaterflowRejectedCount = 0
            };
        }

        var starts = new double[sourcePoints.Count * 2];
        for (int index = 0; index < sourcePoints.Count; index++)
        {
            starts[index * 2] = sourcePoints[index].X;
            starts[(index * 2) + 1] = sourcePoints[index].Y;
        }

        WaterflowTracer.Result traced = WaterflowTracer.Trace(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            starts,
            sourcePoints.Count,
            new WaterflowTracer.Options
            {
                MaxLength = Math.Max(0.0, analysis.MaxLength),
                Tolerance = Math.Max(snapshot.ModelAbsoluteTolerance * 1e-3, 1e-10),
                CancellationRequested = shouldCancel
            });

        int boundaryCount = 0;
        int sinkCount = 0;
        int outputCount = 0;
        string? layerPath = analysis.OutputLayerPath ?? fallbackLayerPath;
        foreach (WaterflowTracer.Path path in traced.Paths)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (path.ReachedBoundary)
                boundaryCount++;
            if (path.TerminatedAtSink)
                sinkCount++;
            if (path.PointCount < 2)
                continue;

            outputCount++;
            var polyline = new Polyline(path.PointCount);
            for (int pointIndex = 0; pointIndex < path.PointCount; pointIndex++)
            {
                polyline.Add(new Point3d(
                    path.PointsXyz[pointIndex * 3],
                    path.PointsXyz[(pointIndex * 3) + 1],
                    path.PointsXyz[(pointIndex * 3) + 2]));
            }

            build.AuxiliaryObjects.Add(new GeneratedRhinoObject
            {
                Geometry = new PolylineCurve(polyline),
                Name = $"{analysis.Label} {outputCount}",
                AnalysisId = analysis.Id,
                ColorArgb = analysis.ColorArgb,
                LayerPath = layerPath
            });
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourcePoints.Count,
            GeneratedOutputCount = outputCount,
            WaterflowBoundaryCount = boundaryCount,
            WaterflowSinkCount = sinkCount,
            WaterflowRejectedCount = traced.RejectedStartCount
        };
    }
}
