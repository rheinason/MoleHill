using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class TerrainAnalysisAnnotationBuilder
{
    public static TerrainAnalysisSummary BuildCurveSlopeSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        CurveSlopeLabelAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        mesh.FaceNormals.ComputeFaceNormals();
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        int sourceCount = 0;
        int outputCount = 0;
        var stats = new ValueStats();
        string unitSuffix = GetSlopeUnitSuffix(analysis.Unit);

        for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (objects[objectIndex].Geometry is not Curve curve)
                continue;

            sourceCount++;
            var divisions = GetCurveDivisionSamples(curve, Math.Max(analysis.Interval, 0.01));
            if (divisions.Count < 2)
                continue;

            double cumulativeDistance = 0.0;
            for (int sampleIndex = 1; sampleIndex < divisions.Count; sampleIndex++)
            {
                if ((sampleIndex & 31) == 0)
                    ThrowIfCancellationRequested(shouldCancel);

                var previous = divisions[sampleIndex - 1];
                var current = divisions[sampleIndex];
                double segmentLength = Math.Max(0.0, curve.GetLength(new Interval(previous.Parameter, current.Parameter)));

                // Label at the segment midpoint, draped onto the terrain. Both the slope magnitude and
                // the arrow come from the terrain normal there (true steepest grade + uphill aspect),
                // independent of the curve's own direction or Z.
                Point3d midXy = Midpoint(previous.Point, current.Point);
                if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, midXy, snapshot.ModelAbsoluteTolerance, out Point3d labelPoint, out var meshPoint) ||
                    meshPoint == null)
                {
                    cumulativeDistance += segmentLength;
                    continue;
                }

                Vector3d normal = GetTerrainSlopeNormal(mesh, meshPoint);
                double slopeRadians = Math.Atan2(Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y)), Math.Abs(normal.Z));
                double slopeRatio = Math.Tan(slopeRadians);
                double slopeValue = SlopeAnalyzer.ConvertRatioToUnit(slopeRatio, analysis.Unit);
                Vector3d direction = GetTerrainSlopeDirection(normal, snapshot.ModelAbsoluteTolerance, analysis.FlipDirection);

                stats.Add(slopeValue);
                outputCount++;

                if (analysis.IsEnabled)
                {
                    double distance = cumulativeDistance + (segmentLength * 0.5);
                    build.AuxiliaryObjects.Add(CreateAnnotationObject(
                        analysis,
                        outputCount,
                        labelPoint,
                        slopeValue,
                        unitSuffix,
                        distance,
                        analysis.BlockDefinitionName,
                        MarkerBlockTemplate.AnnotationSlope,
                        fallbackLayerPath,
                        direction));
                }

                cumulativeDistance += segmentLength;
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildCurveElevationSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        CurveElevationLabelAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        int sourceCount = 0;
        int outputCount = 0;
        var stats = new ValueStats();

        for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (objects[objectIndex].Geometry is not Curve curve)
                continue;

            sourceCount++;
            var divisions = GetCurveDivisionSamples(curve, Math.Max(analysis.Interval, 0.01));
            if (divisions.Count == 0)
                continue;

            double cumulativeDistance = 0.0;
            for (int sampleIndex = 0; sampleIndex < divisions.Count; sampleIndex++)
            {
                if ((sampleIndex & 31) == 0)
                    ThrowIfCancellationRequested(shouldCancel);

                var current = divisions[sampleIndex];
                if (TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, current.Point, snapshot.ModelAbsoluteTolerance, out Point3d worldPoint))
                {
                    stats.Add(worldPoint.Z);
                    outputCount++;

                    if (analysis.IsEnabled)
                    {
                        build.AuxiliaryObjects.Add(CreateAnnotationObject(
                            analysis,
                            outputCount,
                            worldPoint,
                            worldPoint.Z,
                            string.Empty,
                            cumulativeDistance,
                            analysis.BlockDefinitionName,
                            MarkerBlockTemplate.AnnotationElevation,
                            fallbackLayerPath));
                    }
                }

                if (sampleIndex + 1 < divisions.Count)
                    cumulativeDistance += Math.Max(0.0, curve.GetLength(new Interval(current.Parameter, divisions[sampleIndex + 1].Parameter)));
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildProjectedElevationSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        ProjectedElevationLabelAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        int sourceCount = 0;
        int outputCount = 0;
        var stats = new ValueStats();

        foreach (var obj in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (obj.Geometry is not Point && obj.Geometry is not Curve)
                continue;

            sourceCount++;
            foreach (var samplePoint in ExtractElevationSamples(obj.Geometry, snapshot.ModelAbsoluteTolerance))
            {
                if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, samplePoint, snapshot.ModelAbsoluteTolerance, out Point3d worldPoint))
                    continue;

                stats.Add(worldPoint.Z);
                outputCount++;

                if (!analysis.IsEnabled)
                    continue;

                build.AuxiliaryObjects.Add(CreateAnnotationObject(
                    analysis,
                    outputCount,
                    worldPoint,
                    worldPoint.Z,
                    string.Empty,
                    null,
                    analysis.BlockDefinitionName,
                    MarkerBlockTemplate.AnnotationElevation,
                    fallbackLayerPath));
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildPointSlopeSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        PointSlopeLabelAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        mesh.FaceNormals.ComputeFaceNormals();
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        int sourceCount = 0;
        int outputCount = 0;
        var stats = new ValueStats();
        string unitSuffix = GetSlopeUnitSuffix(analysis.Unit);

        foreach (var obj in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (obj.Geometry is not Point point)
                continue;

            sourceCount++;
            if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, point.Location, snapshot.ModelAbsoluteTolerance, out Point3d worldPoint, out var meshPoint))
                continue;

            if (meshPoint == null)
                continue;

            Vector3d normal = GetTerrainSlopeNormal(mesh, meshPoint);
            double slopeRadians = Math.Atan2(Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y)), Math.Abs(normal.Z));
            double slopeRatio = Math.Tan(slopeRadians);
            double slopeValue = SlopeAnalyzer.ConvertRatioToUnit(slopeRatio, analysis.Unit);
            Vector3d direction = GetTerrainSlopeDirection(normal, snapshot.ModelAbsoluteTolerance, analysis.FlipDirection);

            stats.Add(slopeValue);
            outputCount++;

            if (!analysis.IsEnabled)
                continue;

            build.AuxiliaryObjects.Add(CreateAnnotationObject(
                analysis,
                outputCount,
                worldPoint,
                slopeValue,
                unitSuffix,
                null,
                analysis.BlockDefinitionName,
                MarkerBlockTemplate.AnnotationSlope,
                fallbackLayerPath,
                direction));
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildSlopeArrowSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SlopeArrowAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        mesh.FaceNormals.ComputeFaceNormals();
        double tolerance = snapshot.ModelAbsoluteTolerance;
        var boundaries = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, analysis.Sources);
        var bounds = mesh.GetBoundingBox(true);
        int sourceCount = boundaries.Count;
        int outputCount = 0;
        var stats = new ValueStats();
        string unitSuffix = GetSlopeUnitSuffix(analysis.Unit);

        if (!bounds.IsValid)
            return CreateSummary(analysis.Id, sourceCount, outputCount, stats);

        double spacing = Math.Max(analysis.GridSpacing, tolerance * 10.0);

        for (double y = bounds.Min.Y; y <= bounds.Max.Y + (spacing * 0.5); y += spacing)
        {
            ThrowIfCancellationRequested(shouldCancel);
            for (double x = bounds.Min.X; x <= bounds.Max.X + (spacing * 0.5); x += spacing)
            {
                var sampleXy = new Point3d(x, y, 0.0);
                if (boundaries.Count > 0 && !IsInsideAnyBoundary(sampleXy, boundaries, tolerance))
                    continue;

                if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, sampleXy, tolerance, out Point3d worldPoint, out var meshPoint) ||
                    meshPoint == null)
                    continue;

                Vector3d normal = GetTerrainSlopeNormal(mesh, meshPoint);
                Vector3d direction = GetTerrainSlopeDirection(normal, tolerance, analysis.FlipDirection);
                if (!direction.IsValid)
                    continue; // flat node: no meaningful downhill aspect, so no arrow

                double slopeRadians = Math.Atan2(Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y)), Math.Abs(normal.Z));
                double slopeRatio = Math.Tan(slopeRadians);
                double slopeValue = SlopeAnalyzer.ConvertRatioToUnit(slopeRatio, analysis.Unit);

                stats.Add(slopeValue);
                outputCount++;

                if (analysis.IsEnabled)
                {
                    build.AuxiliaryObjects.Add(CreateAnnotationObject(
                        analysis,
                        outputCount,
                        worldPoint,
                        slopeValue,
                        unitSuffix,
                        null,
                        analysis.BlockDefinitionName,
                        MarkerBlockTemplate.AnnotationSlope,
                        fallbackLayerPath,
                        direction));
                }
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildGradeCalloutSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        GradeBetweenPointsAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        double tolerance = snapshot.ModelAbsoluteTolerance;
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        string? layerPath = analysis.OutputLayerPath ?? fallbackLayerPath;
        double textHeight = Math.Max(analysis.TextHeight, 1e-3);
        int sourceCount = 0;
        int outputCount = 0;
        var stats = new ValueStats();

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, curve.PointAtStart, tolerance, out Point3d startPoint) ||
                !TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, curve.PointAtEnd, tolerance, out Point3d endPoint))
                continue;

            double planDistance = startPoint.DistanceTo(new Point3d(endPoint.X, endPoint.Y, startPoint.Z));
            if (planDistance <= tolerance)
                continue;

            // Orient high -> low so the arrow always points downhill.
            Point3d high = startPoint.Z >= endPoint.Z ? startPoint : endPoint;
            Point3d low = startPoint.Z >= endPoint.Z ? endPoint : startPoint;
            double rise = high.Z - low.Z;
            double percent = (rise / planDistance) * 100.0;

            stats.Add(percent);
            outputCount++;

            if (!analysis.IsEnabled)
                continue;

            // Connector chord between the two terrain points.
            build.AuxiliaryObjects.Add(BuildGradeGeometry(analysis, new LineCurve(startPoint, endPoint), $"{analysis.Label} {outputCount}", layerPath));

            // Downhill arrowhead at the low end.
            var descent = new Vector3d(low.X - high.X, low.Y - high.Y, 0.0);
            if (descent.Unitize())
            {
                double size = textHeight * 1.5;
                var perp = Vector3d.CrossProduct(descent, Vector3d.ZAxis);
                perp.Unitize();
                Point3d back = low - (descent * size);
                var head = new Polyline(3)
                {
                    back + (perp * size * 0.4),
                    low,
                    back - (perp * size * 0.4)
                };
                build.AuxiliaryObjects.Add(BuildGradeGeometry(analysis, new PolylineCurve(head), $"{analysis.Label} {outputCount} arrow", layerPath));
            }

            // "1:n (x%)" label at the midpoint, oriented along the connector.
            var mid = new Point3d((startPoint.X + endPoint.X) * 0.5, (startPoint.Y + endPoint.Y) * 0.5, (startPoint.Z + endPoint.Z) * 0.5);
            var along = new Vector3d(endPoint.X - startPoint.X, endPoint.Y - startPoint.Y, 0.0);
            Plane labelPlane = SectionLayoutHelper.FrameFromCurveTangent(mid, along);
            var label = new TextEntity
            {
                Plane = labelPlane,
                PlainText = FormatGradeCallout(rise, planDistance, percent, analysis),
                TextHeight = textHeight,
                Justification = TextJustification.BottomCenter
            };
            build.AuxiliaryObjects.Add(new GeneratedRhinoObject
            {
                Geometry = label,
                Name = $"{analysis.Label} {outputCount} label",
                AnalysisId = analysis.Id,
                ColorArgb = analysis.ColorArgb,
                LayerPath = layerPath
            });
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    private static GeneratedRhinoObject BuildGradeGeometry(GradeBetweenPointsAnalysisDefinition analysis, Curve geometry, string name, string? layerPath)
    {
        return new GeneratedRhinoObject
        {
            Geometry = geometry,
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = layerPath
        };
    }

    private static string FormatGradeCallout(double rise, double planDistance, double percent, GradeBetweenPointsAnalysisDefinition analysis)
    {
        string core;
        if (Math.Abs(rise) <= 1e-9)
        {
            core = "level";
        }
        else
        {
            double n = planDistance / Math.Abs(rise);
            core = $"1:{n.ToString("0.#")} ({FormatValue(percent, analysis.ValueFormat)}%)";
        }

        return string.Concat(analysis.AttributePrefix ?? string.Empty, core, analysis.AttributeSuffix ?? string.Empty);
    }

    public static TerrainAnalysisSummary BuildTerrainSectionSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        TerrainSectionAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        int sourceCount = 0;
        int outputCount = 0;

        var slices = new List<TerrainSectionResult>();
        double maxStation = 0.0;
        double maxRange = 0.0;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            var cutVertices = ApproximateCurveAsPolyline(curve, tolerance);
            if (cutVertices.Count < 2)
                continue;

            var slice = TerrainSectionSlicer.SliceAlongPolyline(mesh, cutVertices, tolerance);
            if (slice.IsEmpty)
                continue;

            slices.Add(slice);
            if (slice.TotalStationLength > maxStation)
                maxStation = slice.TotalStationLength;
            double range = slice.MaximumElevation - slice.MinimumElevation;
            if (range > maxRange)
                maxRange = range;
        }

        double cellWidth = maxStation + Math.Max(maxStation * 0.15, analysis.TextHeight * 8.0);
        double cellHeight = Math.Max(maxRange * 1.4, analysis.TextHeight * 6.0);

        for (int i = 0; i < slices.Count; i++)
        {
            ThrowIfCancellationRequested(shouldCancel);
            var slice = slices[i];
            Plane cellPlane = OffsetCellPlane(insertionPlane, i, columns: Math.Max(slices.Count, 1), cellWidth, cellHeight);

            outputCount += EmitProfileObjects(
                analysis,
                build,
                slice,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: 1.0,
                baseElevation: slice.MinimumElevation,
                showBaseline: true,
                showElevationGrid: analysis.ShowElevationGrid,
                elevationGridInterval: analysis.ElevationGridInterval,
                showStationTicks: analysis.ShowStationTicks,
                stationTickInterval: analysis.StationTickInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationTickInterval,
                textHeight: analysis.TextHeight,
                fallbackLayerPath: fallbackLayerPath,
                sectionLabel: $"{analysis.Label} {i + 1}");
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount
        };
    }

    public static TerrainAnalysisSummary BuildCrossSectionStationSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        CrossSectionStationAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double stationInterval = Math.Max(analysis.StationInterval, tolerance * 100.0);
        double halfWidth = Math.Max(analysis.CrossSectionWidth * 0.5, tolerance * 10.0);
        int gridColumns = Math.Max(analysis.GridColumns, 1);
        double verticalScale = analysis.VerticalExaggeration > 0.0 ? analysis.VerticalExaggeration : 1.0;
        int sourceCount = 0;
        int outputCount = 0;
        int globalIndex = 0;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve alignment)
                continue;

            sourceCount++;
            var stations = GetCurveDivisionSamples(alignment, stationInterval);
            if (stations.Count == 0)
                continue;

            var slices = new List<(double Station, TerrainSectionResult Slice)>(stations.Count);
            double maxStation = 0.0;
            double maxRange = 0.0;

            foreach (var station in stations)
            {
                ThrowIfCancellationRequested(shouldCancel);
                Vector3d tangent = alignment.TangentAt(station.Parameter);
                tangent.Z = 0.0;
                if (!tangent.Unitize())
                    continue;

                var perpendicular = new Vector3d(-tangent.Y, tangent.X, 0.0);
                Point3d a = station.Point - (perpendicular * halfWidth);
                Point3d b = station.Point + (perpendicular * halfWidth);
                a.Z = 0.0;
                b.Z = 0.0;

                var cut = new[] { a, b };
                var slice = TerrainSectionSlicer.SliceAlongPolyline(mesh, cut, tolerance);
                if (slice.IsEmpty)
                    continue;

                slices.Add((alignment.GetLength(new Interval(alignment.Domain.T0, station.Parameter)), slice));
                if (slice.TotalStationLength > maxStation)
                    maxStation = slice.TotalStationLength;
                double range = slice.MaximumElevation - slice.MinimumElevation;
                if (range > maxRange)
                    maxRange = range;
            }

            double cellWidth = analysis.GridCellWidth > 0.0 ? analysis.GridCellWidth : (analysis.CrossSectionWidth + Math.Max(maxStation, analysis.CrossSectionWidth) * 0.1);
            double cellHeight = analysis.GridCellHeight > 0.0 ? analysis.GridCellHeight : Math.Max(maxRange * verticalScale * 1.4, analysis.CrossSectionWidth * 0.3);

            for (int i = 0; i < slices.Count; i++)
            {
                ThrowIfCancellationRequested(shouldCancel);
                var (alignmentStation, slice) = slices[i];
                Plane cellPlane = OffsetCellPlane(insertionPlane, globalIndex, gridColumns, cellWidth, cellHeight);
                globalIndex++;

                if (analysis.ShowCutLinesOnTerrain)
                {
                    foreach (var segment in slice.Segments)
                    {
                        var poly = new Polyline(segment.Vertices.Count);
                        for (int v = 0; v < segment.Vertices.Count; v++)
                            poly.Add(segment.Vertices[v].World);
                        build.AuxiliaryObjects.Add(BuildPolylineObject(analysis, poly, fallbackLayerPath, $"{analysis.Label} cut {globalIndex}", SectionLayerKind.Cuts));
                        outputCount++;
                    }
                }

                outputCount += EmitProfileObjects(
                    analysis,
                    build,
                    slice,
                    cellPlane,
                    horizontalScale: 1.0,
                    verticalScale: verticalScale,
                    baseElevation: slice.MinimumElevation,
                    showBaseline: true,
                    showElevationGrid: analysis.ShowElevationGrid,
                    elevationGridInterval: analysis.ElevationGridInterval,
                    showStationTicks: false,
                    stationTickInterval: 0.0,
                    showStationLabels: analysis.LabelStations,
                    stationLabelInterval: 0.0,
                    textHeight: analysis.TextHeight,
                    fallbackLayerPath: fallbackLayerPath,
                    sectionLabel: $"Sta {alignmentStation:F2}");
            }
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount
        };
    }

    public static TerrainAnalysisSummary BuildLongitudinalSectionSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        LongitudinalSectionAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        string? fallbackLayerPath = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double sampleInterval = Math.Max(analysis.SampleInterval, tolerance * 10.0);
        double verticalScale = analysis.VerticalExaggeration > 0.0 ? analysis.VerticalExaggeration : 1.0;
        int sourceCount = 0;
        int outputCount = 0;
        int sectionIndex = 0;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            var slice = TerrainSectionSlicer.SampleAlongCurve(mesh, curve, sampleInterval, tolerance);
            if (slice.IsEmpty)
                continue;

            sectionIndex++;
            Plane cellPlane = OffsetCellPlane(insertionPlane, sectionIndex - 1, columns: 1, cellWidth: 0.0, cellHeight: 0.0);

            outputCount += EmitProfileObjects(
                analysis,
                build,
                slice,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: verticalScale,
                baseElevation: slice.MinimumElevation,
                showBaseline: analysis.ShowBaseline,
                showElevationGrid: analysis.ShowElevationGrid,
                elevationGridInterval: analysis.ElevationGridInterval,
                showStationTicks: analysis.ShowStationLabels,
                stationTickInterval: analysis.StationLabelInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationLabelInterval,
                textHeight: analysis.TextHeight,
                fallbackLayerPath: fallbackLayerPath,
                sectionLabel: $"{analysis.Label} {sectionIndex}");
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount
        };
    }

    private static int EmitProfileObjects(
        TerrainSectionAnalysisDefinitionBase analysis,
        TerrainBuildResult build,
        TerrainSectionResult slice,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        bool showBaseline,
        bool showElevationGrid,
        double elevationGridInterval,
        bool showStationTicks,
        double stationTickInterval,
        bool showStationLabels,
        double stationLabelInterval,
        double textHeight,
        string? fallbackLayerPath,
        string sectionLabel)
    {
        if (!analysis.IsEnabled)
            return 0;

        int emitted = 0;

        var profilePolylines = SectionLayoutHelper.LayoutFlatAll(slice, cellPlane, horizontalScale, verticalScale, baseElevation);
        foreach (var poly in profilePolylines)
        {
            if (poly.Count < 2)
                continue;
            build.AuxiliaryObjects.Add(BuildPolylineObject(analysis, poly, fallbackLayerPath, sectionLabel, SectionLayerKind.Profile));
            emitted++;
        }

        if (showBaseline && slice.TotalStationLength > 0.0)
        {
            var baseline = SectionLayoutHelper.BuildBaselineAxis(cellPlane, slice.TotalStationLength, horizontalScale, verticalScale, slice.MinimumElevation, baseElevation);
            build.AuxiliaryObjects.Add(BuildLineObject(analysis, baseline, fallbackLayerPath, $"{sectionLabel} baseline", SectionLayerKind.Grid));
            emitted++;
        }

        if (showElevationGrid && elevationGridInterval > 0.0 && slice.TotalStationLength > 0.0)
        {
            var grid = SectionLayoutHelper.BuildElevationGridLines(cellPlane, slice.TotalStationLength, slice.MinimumElevation, slice.MaximumElevation, baseElevation, elevationGridInterval, horizontalScale, verticalScale);
            foreach (var line in grid)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, fallbackLayerPath, $"{sectionLabel} grid", SectionLayerKind.Grid));
                emitted++;
            }
        }

        if (showStationTicks && stationTickInterval > 0.0 && slice.TotalStationLength > 0.0)
        {
            var stations = BuildStationList(slice.TotalStationLength, stationTickInterval);
            double tickHalf = Math.Max(textHeight, 0.1);
            var ticks = SectionLayoutHelper.BuildStationTicks(cellPlane, stations, tickHalf, horizontalScale, verticalScale, baseElevation, slice.MinimumElevation);
            foreach (var line in ticks)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, fallbackLayerPath, $"{sectionLabel} tick", SectionLayerKind.Ticks));
                emitted++;
            }
        }

        if (showStationLabels)
        {
            double labelInterval = stationLabelInterval > 0.0 ? stationLabelInterval : Math.Max(slice.TotalStationLength * 0.25, 1.0);
            var stations = BuildStationList(slice.TotalStationLength, labelInterval);
            double labelOffset = Math.Max(textHeight, 0.1) * 1.5;
            foreach (double station in stations)
            {
                var label = SectionLayoutHelper.BuildLabel(
                    cellPlane,
                    station,
                    slice.MinimumElevation - labelOffset,
                    horizontalScale,
                    verticalScale,
                    baseElevation,
                    station.ToString("F1"),
                    Math.Max(textHeight, 0.05));
                build.AuxiliaryObjects.Add(BuildTextObject(analysis, label, fallbackLayerPath, $"{sectionLabel} {station:F1}", SectionLayerKind.Labels));
                emitted++;
            }
        }

        return emitted;
    }

    private static GeneratedRhinoObject BuildPolylineObject(TerrainSectionAnalysisDefinitionBase analysis, Polyline polyline, string? fallbackLayerPath, string name, SectionLayerKind kind)
    {
        return new GeneratedRhinoObject
        {
            Geometry = new PolylineCurve(polyline),
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = SectionOutputLayers.ResolveLayerPath(analysis.OutputLayerPath, fallbackLayerPath, kind),
            PlotWeight = SectionOutputLayers.GetPlotWeight(kind)
        };
    }

    private static GeneratedRhinoObject BuildLineObject(TerrainSectionAnalysisDefinitionBase analysis, Line line, string? fallbackLayerPath, string name, SectionLayerKind kind)
    {
        return new GeneratedRhinoObject
        {
            Geometry = new LineCurve(line),
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = SectionOutputLayers.ResolveLayerPath(analysis.OutputLayerPath, fallbackLayerPath, kind),
            PlotWeight = SectionOutputLayers.GetPlotWeight(kind)
        };
    }

    private static GeneratedRhinoObject BuildTextObject(TerrainSectionAnalysisDefinitionBase analysis, TextEntity text, string? fallbackLayerPath, string name, SectionLayerKind kind)
    {
        return new GeneratedRhinoObject
        {
            Geometry = text,
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = SectionOutputLayers.ResolveLayerPath(analysis.OutputLayerPath, fallbackLayerPath, kind)
        };
    }

    private static Plane ResolveInsertionPlane(TerrainSectionAnalysisDefinitionBase analysis, RhinoMesh mesh)
    {
        if (analysis.HasInsertionPlane)
        {
            var origin = new Point3d(analysis.InsertionOriginX, analysis.InsertionOriginY, analysis.InsertionOriginZ);
            var xAxis = new Vector3d(analysis.InsertionXAxisX, analysis.InsertionXAxisY, analysis.InsertionXAxisZ);
            var yAxis = new Vector3d(analysis.InsertionYAxisX, analysis.InsertionYAxisY, analysis.InsertionYAxisZ);
            if (xAxis.Unitize() && yAxis.Unitize())
                return new Plane(origin, xAxis, yAxis);
        }

        var bounds = mesh.GetBoundingBox(true);
        if (bounds.IsValid)
        {
            double offset = Math.Max((bounds.Max.Y - bounds.Min.Y) * 0.25, 1.0);
            var origin = new Point3d(bounds.Min.X, bounds.Min.Y - offset, bounds.Min.Z);
            return new Plane(origin, Vector3d.XAxis, Vector3d.YAxis);
        }

        return Plane.WorldXY;
    }

    private static Plane OffsetCellPlane(Plane basePlane, int cellIndex, int columns, double cellWidth, double cellHeight)
    {
        if (columns <= 0 || (cellWidth <= 0.0 && cellHeight <= 0.0))
            return basePlane;

        int col = cellIndex % columns;
        int row = cellIndex / columns;
        Point3d origin = basePlane.Origin
            + (basePlane.XAxis * (col * cellWidth))
            + (basePlane.YAxis * (-row * cellHeight));
        return new Plane(origin, basePlane.XAxis, basePlane.YAxis);
    }

    private static IReadOnlyList<Point3d> ApproximateCurveAsPolyline(Curve curve, double tolerance)
    {
        if (curve.TryGetPolyline(out Polyline existing) && existing != null && existing.Count >= 2)
            return existing.ToArray();

        double length = curve.GetLength();
        if (length <= tolerance)
            return Array.Empty<Point3d>();

        double spacing = Math.Max(length / 256.0, tolerance * 4.0);
        var samples = new List<Point3d> { curve.PointAtStart };
        if (curve.DivideByLength(spacing, true) is { Length: > 0 } parameters)
        {
            foreach (double parameter in parameters)
            {
                var point = curve.PointAt(parameter);
                if (samples[samples.Count - 1].DistanceToSquared(point) > tolerance * tolerance)
                    samples.Add(point);
            }
        }
        var endPoint = curve.PointAtEnd;
        if (samples[samples.Count - 1].DistanceToSquared(endPoint) > tolerance * tolerance)
            samples.Add(endPoint);
        return samples;
    }

    private static List<double> BuildStationList(double totalLength, double interval)
    {
        var stations = new List<double>();
        if (totalLength <= 0.0 || interval <= 0.0)
            return stations;

        for (double s = 0.0; s <= totalLength + (interval * 0.5); s += interval)
            stations.Add(Math.Min(s, totalLength));

        if (stations.Count == 0 || Math.Abs(stations[stations.Count - 1] - totalLength) > 1e-6)
            stations.Add(totalLength);

        return stations;
    }

    private static GeneratedRhinoObject CreateAnnotationObject(
        BlockAttributeAnalysisDefinition analysis,
        int index,
        Point3d worldPoint,
        double rawValue,
        string unitSuffix,
        double? distance,
        string? blockDefinitionName,
        MarkerBlockTemplate template,
        string? fallbackLayerPath = null,
        Vector3d? direction = null)
    {
        string formattedValue = FormatValue(rawValue, analysis.ValueFormat);
        string displayValue = string.Concat(
            analysis.AttributePrefix ?? string.Empty,
            formattedValue,
            analysis.AttributeSuffix ?? string.Empty);

        var userStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [GeneratedBlockCatalog.DisplayToken] = displayValue,
            [GeneratedBlockCatalog.ValueToken] = formattedValue,
            [GeneratedBlockCatalog.PrefixToken] = analysis.AttributePrefix ?? string.Empty,
            [GeneratedBlockCatalog.SuffixToken] = analysis.AttributeSuffix ?? string.Empty,
            [GeneratedBlockCatalog.UnitToken] = unitSuffix,
            [GeneratedBlockCatalog.NameToken] = analysis.Label,
            [GeneratedBlockCatalog.IndexToken] = index.ToString(),
            [GeneratedBlockCatalog.DistanceToken] = distance.HasValue ? FormatValue(distance.Value, "F2") : string.Empty
        };

        return new GeneratedRhinoObject
        {
            Name = $"{analysis.Label} {index}",
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = analysis.OutputLayerPath ?? fallbackLayerPath,
            InstanceDefinitionName = string.IsNullOrWhiteSpace(blockDefinitionName)
                ? GeneratedBlockCatalog.GetDefaultDefinitionName(template)
                : blockDefinitionName,
            MarkerBlockTemplate = template,
            InstanceUserStrings = userStrings,
            InstanceTransform = CreateInstanceTransform(worldPoint, Math.Max(analysis.BlockScale, 0.01), direction)
        };
    }

    private static TerrainAnalysisSummary CreateSummary(Guid analysisId, int sourceCount, int outputCount, ValueStats stats)
    {
        return new TerrainAnalysisSummary
        {
            AnalysisId = analysisId,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SampleMinValue = stats.Count > 0 ? stats.Min : 0.0,
            SampleMaxValue = stats.Count > 0 ? stats.Max : 0.0,
            SampleAverageValue = stats.Count > 0 ? stats.Sum / stats.Count : 0.0
        };
    }

    private static List<(double Parameter, Point3d Point)> GetCurveDivisionSamples(Curve curve, double interval)
    {
        var samples = new List<(double Parameter, Point3d Point)>
        {
            (curve.Domain.T0, curve.PointAtStart)
        };

        if (curve.DivideByLength(interval, true) is { Length: > 0 } parameters)
        {
            foreach (double parameter in parameters)
                AppendUniqueParameterPoint(samples, parameter, curve.PointAt(parameter));
        }

        AppendUniqueParameterPoint(samples, curve.Domain.T1, curve.PointAtEnd);
        return samples;
    }

    private static IEnumerable<Point3d> ExtractElevationSamples(GeometryBase geometry, double tolerance)
    {
        switch (geometry)
        {
            case Point point:
                yield return point.Location;
                yield break;
            case Curve curve:
            {
                var points = new List<Point3d>();
                using var nurbsCurve = curve.ToNurbsCurve();
                if (nurbsCurve != null)
                {
                    foreach (var editPoint in nurbsCurve.GrevillePoints())
                        AppendUniquePoint(points, editPoint, tolerance);
                }

                AppendUniquePoint(points, curve.PointAtStart, tolerance);
                AppendUniquePoint(points, curve.PointAtEnd, tolerance);
                foreach (var point3d in points)
                    yield return point3d;
                yield break;
            }
        }
    }

    private static void AppendUniqueParameterPoint(List<(double Parameter, Point3d Point)> samples, double parameter, Point3d point)
    {
        if (samples.Count > 0)
        {
            var last = samples[^1];
            if (Math.Abs(last.Parameter - parameter) <= RhinoMath.ZeroTolerance ||
                last.Point.DistanceToSquared(point) <= RhinoMath.ZeroTolerance)
                return;
        }

        samples.Add((parameter, point));
    }

    private static void AppendUniquePoint(List<Point3d> points, Point3d point, double tolerance)
    {
        double squaredTolerance = tolerance <= 0.0 ? RhinoMath.ZeroTolerance : tolerance * tolerance;
        foreach (var existing in points)
        {
            if (existing.DistanceToSquared(point) <= squaredTolerance)
                return;
        }

        points.Add(point);
    }

    private static Point3d Midpoint(Point3d a, Point3d b)
    {
        return new Point3d(
            (a.X + b.X) * 0.5,
            (a.Y + b.Y) * 0.5,
            (a.Z + b.Z) * 0.5);
    }

    private static Transform CreateInstanceTransform(Point3d worldPoint, double scale, Vector3d? direction)
    {
        var scaleTransform = Transform.Scale(Point3d.Origin, scale);
        if (!direction.HasValue)
            return Transform.Translation(worldPoint - Point3d.Origin) * scaleTransform;

        Vector3d xAxis = direction.Value;
        xAxis.Z = 0.0;
        if (!xAxis.Unitize())
            return Transform.Translation(worldPoint - Point3d.Origin) * scaleTransform;

        Vector3d yAxis = Vector3d.CrossProduct(Vector3d.ZAxis, xAxis);
        if (!yAxis.Unitize())
            return Transform.Translation(worldPoint - Point3d.Origin) * scaleTransform;

        var plane = new Plane(worldPoint, xAxis, yAxis);
        return Transform.PlaneToPlane(Plane.WorldXY, plane) * scaleTransform;
    }

    private static Vector3d GetTerrainSlopeDirection(Vector3d normal, double tolerance, bool flip)
    {
        if (normal.Z < 0.0)
            normal = -normal;

        var direction = new Vector3d(-normal.X, -normal.Y, 0.0);
        if (flip)
            direction = -direction;
        return direction.Length <= tolerance
            ? Vector3d.Unset
            : direction;
    }

    private static Vector3d GetTerrainSlopeNormal(RhinoMesh mesh, MeshPoint meshPoint)
    {
        int faceIndex = meshPoint.FaceIndex;
        if (faceIndex >= 0 && faceIndex < mesh.FaceNormals.Count)
        {
            Vector3d faceNormal = mesh.FaceNormals[faceIndex];
            if (faceNormal.IsValid && faceNormal.Unitize())
                return faceNormal;
        }

        return mesh.NormalAt(meshPoint);
    }

    private static bool IsInsideAnyBoundary(Point3d point, IReadOnlyList<Curve> boundaries, double tolerance)
    {
        foreach (var curve in boundaries)
        {
            if (curve == null || !curve.IsClosed)
                continue;

            var containment = curve.Contains(new Point3d(point.X, point.Y, curve.PointAtStart.Z), Plane.WorldXY, Math.Max(tolerance, RhinoMath.ZeroTolerance));
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                return true;
        }

        return false;
    }

    private static string GetSlopeUnitSuffix(SlopeAnalyzer.SlopeUnit unit)
    {
        return unit switch
        {
            SlopeAnalyzer.SlopeUnit.Degrees => "deg",
            SlopeAnalyzer.SlopeUnit.Promille => "promille",
            SlopeAnalyzer.SlopeUnit.Ratio => string.Empty,
            _ => "%"
        };
    }

    private static string FormatValue(double value, string format)
    {
        try
        {
            return value.ToString(format);
        }
        catch (FormatException)
        {
            return value.ToString("G4");
        }
    }

    private static void ThrowIfCancellationRequested(Func<bool>? shouldCancel)
    {
        if (shouldCancel?.Invoke() == true)
            throw new OperationCanceledException();
    }

    private struct ValueStats
    {
        public int Count { get; private set; }
        public double Sum { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }

        public void Add(double value)
        {
            if (Count == 0)
            {
                Min = value;
                Max = value;
            }
            else
            {
                Min = Math.Min(Min, value);
                Max = Math.Max(Max, value);
            }

            Count++;
            Sum += value;
        }
    }
}
