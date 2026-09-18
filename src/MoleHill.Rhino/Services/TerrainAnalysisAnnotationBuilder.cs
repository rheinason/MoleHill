using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class TerrainAnalysisAnnotationBuilder
{
    private sealed record SectionTerrainProfile(
        Guid TerrainId,
        string TerrainName,
        int ColorArgb,
        TerrainSectionResult Slice,
        bool IsOwner);

    private readonly record struct SectionEmissionStats(int OutputCount, int CutRegions, int FillRegions);

    public static TerrainAnalysisSummary BuildCurveSlopeSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        CurveSlopeLabelAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
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
            var divisions = GetCurveDivisionSamples(curve, Math.Max(analysis.Interval, MinimumLength(snapshot)));
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
                if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, midXy, snapshot.ModelAbsoluteTolerance, out Point3d labelPoint, out int faceIndex) ||
                    !TryGetTerrainSlopeNormal(mesh, faceIndex, out Vector3d normal))
                {
                    cumulativeDistance += segmentLength;
                    continue;
                }

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
                        snapshot,
                        analysis,
                        outputCount,
                        labelPoint,
                        slopeValue,
                        unitSuffix,
                        distance,
                        analysis.BlockDefinitionName,
                        MarkerBlockTemplate.AnnotationSlope,
                        layerRoles,
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
        CurveElevationLabelAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
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
            var divisions = GetCurveDivisionSamples(curve, Math.Max(analysis.Interval, MinimumLength(snapshot)));
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
                            snapshot,
                            analysis,
                            outputCount,
                            worldPoint,
                            worldPoint.Z,
                            string.Empty,
                            cumulativeDistance,
                            analysis.BlockDefinitionName,
                            MarkerBlockTemplate.AnnotationElevation,
                            layerRoles));
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
        ProjectedElevationLabelAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
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
                    snapshot,
                    analysis,
                    outputCount,
                    worldPoint,
                    worldPoint.Z,
                    string.Empty,
                    null,
                    analysis.BlockDefinitionName,
                    MarkerBlockTemplate.AnnotationElevation,
                    layerRoles));
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildPointSlopeSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        PointSlopeLabelAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
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
            if (obj.Geometry is not Point && obj.Geometry is not Curve)
                continue;

            sourceCount++;
            foreach (var samplePoint in ExtractElevationSamples(obj.Geometry, snapshot.ModelAbsoluteTolerance))
            {
                if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, samplePoint, snapshot.ModelAbsoluteTolerance, out Point3d worldPoint, out int faceIndex) ||
                    !TryGetTerrainSlopeNormal(mesh, faceIndex, out Vector3d normal))
                    continue;

                double slopeRadians = Math.Atan2(Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y)), Math.Abs(normal.Z));
                double slopeRatio = Math.Tan(slopeRadians);
                double slopeValue = SlopeAnalyzer.ConvertRatioToUnit(slopeRatio, analysis.Unit);
                Vector3d direction = GetTerrainSlopeDirection(normal, snapshot.ModelAbsoluteTolerance, analysis.FlipDirection);

                stats.Add(slopeValue);
                outputCount++;

                if (!analysis.IsEnabled)
                    continue;

                build.AuxiliaryObjects.Add(CreateAnnotationObject(
                    snapshot,
                    analysis,
                    outputCount,
                    worldPoint,
                    slopeValue,
                    unitSuffix,
                    null,
                    analysis.BlockDefinitionName,
                    MarkerBlockTemplate.AnnotationSlope,
                    layerRoles,
                    direction));
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildSlopeArrowSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SlopeArrowAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
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

                if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, sampleXy, tolerance, out Point3d worldPoint, out int faceIndex) ||
                    !TryGetTerrainSlopeNormal(mesh, faceIndex, out Vector3d normal))
                    continue;

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
                        snapshot,
                        analysis,
                        outputCount,
                        worldPoint,
                        slopeValue,
                        unitSuffix,
                        null,
                        analysis.BlockDefinitionName,
                        MarkerBlockTemplate.AnnotationSlope,
                        layerRoles,
                        direction));
                }
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildGradeCalloutSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        GradeBetweenPointsAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
    {
        double tolerance = snapshot.ModelAbsoluteTolerance;
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        string layerPath = Roles(layerRoles).Path(LayerRole.Labels);
        double textHeight = Math.Max(
            analysis.FollowsAnnotationStyle
                ? snapshot.AnnotationStyle.TextHeight
                : analysis.TextHeight,
            MinimumLength(snapshot));
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
                Role = LayerRole.Labels,
                Geometry = label,
                Name = $"{analysis.Label} {outputCount} label",
                AnalysisId = analysis.Id,
                ColorArgb = analysis.ColorArgb,
                LayerPath = layerPath
            });
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    private static GeneratedRhinoObject BuildGradeGeometry(GradeBetweenPointsAnnotationDefinition analysis, Curve geometry, string name, string? layerPath)
    {
        return new GeneratedRhinoObject
        {
            Role = LayerRole.Labels,
            Geometry = geometry,
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = layerPath
        };
    }

    private static string FormatGradeCallout(double rise, double planDistance, double percent, GradeBetweenPointsAnnotationDefinition analysis)
    {
        string core;
        if (Math.Abs(rise) <= Math.Max(Math.Abs(planDistance) * 1e-12, double.Epsilon))
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
        TerrainSectionAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        int sourceCount = 0;
        int outputCount = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);

        var sectionProfiles = new List<List<SectionTerrainProfile>>();
        var sectionCuts = new List<SectionCutGeometry>();
        double maxStation = 0.0;
        double maxRange = 0.0;
        int availableTerrainCount = 1;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            var cutVertices = ApproximateCurveAsPolyline(curve, tolerance);
            if (cutVertices.Count < 2)
                continue;

            List<SectionTerrainProfile> profiles = SliceTerrainsAlongPolyline(
                snapshot, mesh, analysis, cutVertices, tolerance);
            if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                continue;

            sectionProfiles.Add(profiles);
            sectionCuts.Add(SectionCutGeometry.AlongPolyline(cutVertices));
            availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
            double minimum = profiles.Min(profile => profile.Slice.MinimumElevation);
            double maximum = profiles.Max(profile => profile.Slice.MaximumElevation);
            maxStation = Math.Max(maxStation, profiles.Max(profile => profile.Slice.TotalStationLength));
            double range = maximum - minimum;
            if (range > maxRange)
                maxRange = range;
        }

        double cellWidth = maxStation + Math.Max(maxStation * 0.15, ResolveTextHeight(snapshot, analysis) * 8.0);
        double cellHeight = Math.Max(maxRange * 1.4, ResolveTextHeight(snapshot, analysis) * 6.0);

        int cutRegions = 0;
        int fillRegions = 0;
        for (int i = 0; i < sectionProfiles.Count; i++)
        {
            ThrowIfCancellationRequested(shouldCancel);
            List<SectionTerrainProfile> profiles = sectionProfiles[i];
            Plane cellPlane = OffsetCellPlane(insertionPlane, i, columns: Math.Max(sectionProfiles.Count, 1), cellWidth, cellHeight);

            SectionEmissionStats emitted = EmitCombinedProfileObjects(
                snapshot,
                sectionCuts[i],
                analysis,
                build,
                profiles,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: ResolveVerticalExaggeration(analysis),
                baseElevation: profiles.Min(profile => profile.Slice.MinimumElevation),
                comparisonTolerance: tolerance,
                showBaseline: true,
                showElevationGrid: analysis.ShowElevationGrid,
                elevationGridInterval: analysis.ElevationGridInterval,
                showStationTicks: analysis.ShowStationTicks,
                stationTickInterval: analysis.StationTickInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationTickInterval,
                textHeight: ResolveTextHeight(snapshot, analysis),
                layerRoles: layerRoles,
                sectionLabel: $"{analysis.Label} {i + 1}",
                hatchPatterns: snapshot.HatchPatterns,
                    baseMesh: baseMesh);
            outputCount += emitted.OutputCount;
            cutRegions += emitted.CutRegions;
            fillRegions += emitted.FillRegions;
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    public static TerrainAnalysisSummary BuildCrossSectionStationSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        CrossSectionStationAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double stationInterval = Math.Max(analysis.StationInterval, tolerance * 100.0);
        double halfWidth = Math.Max(analysis.CrossSectionWidth * 0.5, tolerance * 10.0);
        int gridColumns = Math.Max(analysis.GridColumns, 1);
        double verticalScale = ResolveVerticalExaggeration(analysis);
        int sourceCount = 0;
        int outputCount = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);
        int globalIndex = 0;
        int availableTerrainCount = 1;
        int cutRegions = 0;
        int fillRegions = 0;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve alignment)
                continue;

            sourceCount++;
            var stations = GetCurveDivisionSamples(alignment, stationInterval);
            if (stations.Count == 0)
                continue;

            var slices = new List<(double Station, List<SectionTerrainProfile> Profiles, SectionCutGeometry Cut)>(stations.Count);
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
                List<SectionTerrainProfile> profiles = SliceTerrainsAlongPolyline(
                    snapshot, mesh, analysis, cut, tolerance);
                if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                    continue;

                slices.Add((
                    alignment.GetLength(new Interval(alignment.Domain.T0, station.Parameter)),
                    profiles,
                    SectionCutGeometry.AlongPolyline(cut)));
                availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
                maxStation = Math.Max(maxStation, profiles.Max(profile => profile.Slice.TotalStationLength));
                double range = profiles.Max(profile => profile.Slice.MaximumElevation) -
                               profiles.Min(profile => profile.Slice.MinimumElevation);
                if (range > maxRange)
                    maxRange = range;
            }

            double cellWidth = analysis.GridCellWidth > 0.0 ? analysis.GridCellWidth : (analysis.CrossSectionWidth + Math.Max(maxStation, analysis.CrossSectionWidth) * 0.1);
            double cellHeight = analysis.GridCellHeight > 0.0 ? analysis.GridCellHeight : Math.Max(maxRange * verticalScale * 1.4, analysis.CrossSectionWidth * 0.3);

            for (int i = 0; i < slices.Count; i++)
            {
                ThrowIfCancellationRequested(shouldCancel);
                var (alignmentStation, profiles, _) = slices[i];
                Plane cellPlane = OffsetCellPlane(insertionPlane, globalIndex, gridColumns, cellWidth, cellHeight);
                globalIndex++;

                if (analysis.ShowCutLinesOnTerrain)
                {
                    foreach (SectionTerrainProfile profile in profiles)
                    {
                        foreach (TerrainSectionSegment segment in profile.Slice.Segments)
                        {
                            var poly = new Polyline(segment.Vertices.Count);
                            for (int v = 0; v < segment.Vertices.Count; v++)
                                poly.Add(segment.Vertices[v].World);
                            build.AuxiliaryObjects.Add(BuildPolylineObject(
                                analysis,
                                poly,
                                layerRoles,
                                $"{analysis.Label} {profile.TerrainName} cut {globalIndex}",
                                LayerRole.SectionsCuts,
                                profile.ColorArgb));
                            outputCount++;
                        }
                    }
                }

                SectionEmissionStats emitted = EmitCombinedProfileObjects(
                    snapshot,
                    slices[i].Cut,
                    analysis,
                    build,
                    profiles,
                    cellPlane,
                    horizontalScale: 1.0,
                    verticalScale: verticalScale,
                    baseElevation: profiles.Min(profile => profile.Slice.MinimumElevation),
                    comparisonTolerance: tolerance,
                    showBaseline: true,
                    showElevationGrid: analysis.ShowElevationGrid,
                    elevationGridInterval: analysis.ElevationGridInterval,
                    showStationTicks: false,
                    stationTickInterval: 0.0,
                    showStationLabels: analysis.LabelStations,
                    stationLabelInterval: 0.0,
                    textHeight: ResolveTextHeight(snapshot, analysis),
                    layerRoles: layerRoles,
                    sectionLabel: $"Sta {alignmentStation:F2}",
                    hatchPatterns: snapshot.HatchPatterns,
                    baseMesh: baseMesh);
                outputCount += emitted.OutputCount;
                cutRegions += emitted.CutRegions;
                fillRegions += emitted.FillRegions;
            }
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    public static TerrainAnalysisSummary BuildLongitudinalSectionSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        LongitudinalSectionAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double sampleInterval = Math.Max(analysis.SampleInterval, tolerance * 10.0);
        double verticalScale = ResolveVerticalExaggeration(analysis);
        int sourceCount = 0;
        int outputCount = 0;
        int sectionIndex = 0;
        int availableTerrainCount = 1;
        int cutRegions = 0;
        int fillRegions = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            List<SectionTerrainProfile> profiles = SampleTerrainsAlongCurve(
                snapshot, mesh, analysis, curve, sampleInterval, tolerance);
            if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                continue;

            sectionIndex++;
            Plane cellPlane = OffsetCellPlane(insertionPlane, sectionIndex - 1, columns: 1, cellWidth: 0.0, cellHeight: 0.0);

            availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
            SectionEmissionStats emitted = EmitCombinedProfileObjects(
                snapshot,
                SectionCutGeometry.AlongCurve(curve, sampleInterval),
                analysis,
                build,
                profiles,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: verticalScale,
                baseElevation: profiles.Min(profile => profile.Slice.MinimumElevation),
                comparisonTolerance: tolerance,
                showBaseline: analysis.ShowBaseline,
                showElevationGrid: analysis.ShowElevationGrid,
                elevationGridInterval: analysis.ElevationGridInterval,
                showStationTicks: analysis.ShowStationLabels,
                stationTickInterval: analysis.StationLabelInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationLabelInterval,
                textHeight: ResolveTextHeight(snapshot, analysis),
                layerRoles: layerRoles,
                sectionLabel: $"{analysis.Label} {sectionIndex}",
                hatchPatterns: snapshot.HatchPatterns,
                    baseMesh: baseMesh);
            outputCount += emitted.OutputCount;
            cutRegions += emitted.CutRegions;
            fillRegions += emitted.FillRegions;
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    private static SectionEmissionStats EmitCombinedProfileObjects(
        TerrainBuildSnapshot snapshot,
        SectionCutGeometry cutGeometry,
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build,
        IReadOnlyList<SectionTerrainProfile> profiles,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        double comparisonTolerance,
        bool showBaseline,
        bool showElevationGrid,
        double elevationGridInterval,
        bool showStationTicks,
        double stationTickInterval,
        bool showStationLabels,
        double stationLabelInterval,
        double textHeight,
        LayerRoleTable? layerRoles,
        string sectionLabel,
        HatchPatternSnapshot hatchPatterns,
        RhinoMesh? baseMesh)
    {
        if (!analysis.IsEnabled)
            return default;

        int emitted = 0;
        int cutRegions = 0;
        int fillRegions = 0;
        TerrainSectionResult ownerSlice = profiles[0].Slice;
        double totalStation = profiles.Max(profile => profile.Slice.TotalStationLength);
        double minimumElevation = profiles.Min(profile => profile.Slice.MinimumElevation);
        double maximumElevation = profiles.Max(profile => profile.Slice.MaximumElevation);
        double effectiveElevationGridInterval = SectionLayoutHelper.ResolveElevationGridSpacing(
            minimumElevation,
            maximumElevation,
            elevationGridInterval);

        TerrainSectionResult? referenceSliceForProfile = null;
        if (analysis.ShowCutFillRegions)
        {
            TerrainSectionResult? referenceSlice = ResolveCutFillReferenceSlice(
                snapshot, cutGeometry, analysis, profiles, comparisonTolerance, build, baseMesh);
            referenceSliceForProfile = referenceSlice;
            if (referenceSlice != null)
            {
                IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
                    ownerSlice,
                    referenceSlice,
                    Math.Max(comparisonTolerance, totalStation * 1e-10));
                foreach (SectionComparisonRegion region in regions)
                {
                    bool isCut = region.IsCut;
                    LayerRole regionRole = isCut ? LayerRole.SectionsCutFillCut : LayerRole.SectionsCutFillFill;
                    LayerAppearance regionAppearance = Roles(layerRoles).Appearance(regionRole);
                    string regionLayerPath = Roles(layerRoles).Path(regionRole);

                    // Pattern, scale and rotation come from the role, so every section in a document
                    // fills the same way and the office controls it from one place. The analysis's own
                    // fields are only a fallback for a document whose template predates them.
                    string? patternName = regionAppearance.HatchPatternName
                        ?? (isCut ? analysis.CutHatchPatternName : analysis.FillHatchPatternName);
                    string defaultPatternName = isCut
                        ? HatchPatternService.DefaultCutPatternName
                        : HatchPatternService.DefaultFillPatternName;

                    // A hatch, not a transparent mesh: a shaded mesh is a rendering artefact that does not
                    // print and ignores the document hatch scale.
                    IReadOnlyList<Hatch> regionHatches = BuildComparisonRegionHatch(
                        region,
                        cellPlane,
                        horizontalScale,
                        verticalScale,
                        baseElevation,
                        hatchPatterns.ResolveIndex(patternName, defaultPatternName),
                        hatchPatterns.ResolveScale(
                            patternName,
                            defaultPatternName,
                            regionAppearance.HatchScale,
                            textHeight),
                        regionAppearance.HatchRotationDegrees,
                        comparisonTolerance);
                    if (regionHatches.Count == 0)
                        continue;
                    foreach (Hatch regionHatch in regionHatches)
                    {
                        build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                        {
                            Role = regionRole,
                            Geometry = regionHatch,
                            Name = $"{sectionLabel} {(isCut ? "cut" : "fill")}",
                            AnalysisId = analysis.Id,
                            AppearanceSource = GeneratedAppearanceSource.Layer,
                            LayerPath = regionLayerPath,
                            DisplayOrder = SectionDisplayOrder.Fill
                        });
                        emitted++;
                    }

                    // Region counts stay per comparison region: one region may need several hatches.
                    if (isCut)
                        cutRegions++;
                    else
                        fillRegions++;
                }
            }
        }

        // Existing ground, drawn from whatever the cut/fill comparison measured against. It is context:
        // a light line the proposed profile is read against. Without it the original ground is only ever
        // implied by the far edge of a hatch, so it vanishes wherever nothing was cut or filled.
        if (referenceSliceForProfile != null)
        {
            foreach (Polyline existing in SectionLayoutHelper.LayoutFlatAll(
                         referenceSliceForProfile, cellPlane, horizontalScale, verticalScale, baseElevation))
            {
                if (existing.Count < 2)
                    continue;

                build.AuxiliaryObjects.Add(BuildPolylineObject(
                    analysis,
                    existing,
                    layerRoles,
                    $"{sectionLabel} existing ground",
                    LayerRole.SectionsExisting,
                    colorArgbOverride: null));
                emitted++;
            }
        }

        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            SectionTerrainProfile profile = profiles[profileIndex];

            // The sectioned terrain itself — the finished modifier stack — is the subject of the drawing,
            // so it is the heaviest line on it and takes its appearance from the layer: black, by
            // convention, rather than the terrain's preview tint, which is a screen colour and prints as
            // whatever pastel it happens to be. Additional comparison terrains keep their own colours,
            // which is the only thing telling them apart.
            bool isOwnerProfile = profileIndex == 0;

            foreach (Polyline poly in SectionLayoutHelper.LayoutFlatAll(
                         profile.Slice, cellPlane, horizontalScale, verticalScale, baseElevation))
            {
                if (poly.Count < 2)
                    continue;

                build.AuxiliaryObjects.Add(BuildPolylineObject(
                    analysis,
                    poly,
                    layerRoles,
                    $"{sectionLabel} {profile.TerrainName}",
                    LayerRole.Sections,
                    isOwnerProfile ? null : profile.ColorArgb));
                emitted++;
            }
        }

        if (showBaseline && totalStation > 0.0)
        {
            var baseline = SectionLayoutHelper.BuildBaselineAxis(cellPlane, totalStation, horizontalScale, verticalScale, minimumElevation, baseElevation);
            build.AuxiliaryObjects.Add(BuildLineObject(analysis, baseline, layerRoles, $"{sectionLabel} baseline", LayerRole.SectionsGrid));
            emitted++;
        }

        if (showElevationGrid && effectiveElevationGridInterval > 0.0 && totalStation > 0.0)
        {
            var grid = SectionLayoutHelper.BuildElevationGridLines(cellPlane, totalStation, minimumElevation, maximumElevation, baseElevation, effectiveElevationGridInterval, horizontalScale, verticalScale);
            foreach (var line in grid)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, layerRoles, $"{sectionLabel} grid", LayerRole.SectionsGrid));
                emitted++;
            }
        }

        if (showStationTicks && stationTickInterval > 0.0 && totalStation > 0.0)
        {
            var stations = BuildStationList(totalStation, stationTickInterval);
            double tickHalf = Math.Max(textHeight, double.Epsilon);
            var ticks = SectionLayoutHelper.BuildStationTicks(cellPlane, stations, tickHalf, horizontalScale, verticalScale, baseElevation, minimumElevation);
            foreach (var line in ticks)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, layerRoles, $"{sectionLabel} tick", LayerRole.SectionsTicks));
                emitted++;
            }
        }

        if (showStationLabels)
        {
            double labelInterval = stationLabelInterval > 0.0 ? stationLabelInterval : totalStation * 0.25;
            var stations = BuildStationList(totalStation, labelInterval);
            double labelOffset = Math.Max(textHeight, double.Epsilon) * 1.5;
            foreach (double station in stations)
            {
                var label = SectionLayoutHelper.BuildLabel(
                    cellPlane,
                    station,
                    minimumElevation - labelOffset,
                    horizontalScale,
                    verticalScale,
                    baseElevation,
                    station.ToString("F1"),
                    Math.Max(textHeight, double.Epsilon));
                build.AuxiliaryObjects.Add(BuildTextObject(analysis, label, layerRoles, $"{sectionLabel} {station:F1}", LayerRole.SectionsLabels));
                emitted++;
            }
        }

        return new SectionEmissionStats(emitted, cutRegions, fillRegions);
    }

    /// <summary>
    /// How the terrain was cut for one section cell, so an arbitrary reference mesh can be cut the same
    /// way. A polyline cut and a sampled-along-curve cut produce different station parametrizations, and
    /// comparing profiles built two different ways would misreport every depth.
    /// </summary>
    private readonly record struct SectionCutGeometry(
        IReadOnlyList<Point3d>? CutVertices,
        Curve? SampledCurve,
        double SampleInterval)
    {
        public static SectionCutGeometry AlongPolyline(IReadOnlyList<Point3d> cutVertices) =>
            new(cutVertices, null, 0.0);

        public static SectionCutGeometry AlongCurve(Curve curve, double sampleInterval) =>
            new(null, curve, sampleInterval);

        /// <summary>Cuts a mesh exactly as the terrain was cut. Null when the mesh misses the cut.</summary>
        public TerrainSectionResult? Slice(RhinoMesh mesh, double tolerance)
        {
            TerrainSectionResult slice = SampledCurve != null
                ? TerrainSectionSlicer.SampleAlongCurve(mesh, SampledCurve, SampleInterval, tolerance)
                : TerrainSectionSlicer.SliceAlongPolyline(mesh, CutVertices!, tolerance);
            return slice.IsEmpty ? null : slice;
        }
    }

    /// <summary>
    /// The existing-ground profile to shade cut and fill against: explicitly referenced Rhino geometry
    /// first, then another MoleHill terrain. Returns null — with a diagnostic saying why — when cut/fill
    /// is switched on but nothing usable is configured, which used to fail silently and read as "the hatch
    /// does not work".
    /// </summary>
    private static TerrainSectionResult? ResolveCutFillReferenceSlice(
        TerrainBuildSnapshot snapshot,
        SectionCutGeometry cutGeometry,
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<SectionTerrainProfile> profiles,
        double tolerance,
        TerrainBuildResult build,
        RhinoMesh? baseMesh)
    {
        if (analysis.CutFillReference.HasReferences)
        {
            var meshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, analysis.CutFillReference);
            if (meshes.Count == 0)
            {
                build.Diagnostics.Add(
                    $"{analysis.Label}: cut/fill reference resolved no mesh geometry; no cut or fill was shaded.");
                return null;
            }

            RhinoMesh combined = meshes.Count == 1 ? meshes[0] : CombineMeshes(meshes);
            TerrainSectionResult? slice = cutGeometry.Slice(combined, tolerance);
            if (slice == null)
            {
                build.Diagnostics.Add(
                    $"{analysis.Label}: the cut/fill reference does not reach this section line; no cut or fill was shaded.");
            }

            return slice;
        }

        if (analysis.CutFillReferenceTerrainId.HasValue)
        {
            SectionTerrainProfile? referenceProfile = profiles.FirstOrDefault(
                profile => profile.TerrainId == analysis.CutFillReferenceTerrainId.Value);
            if (referenceProfile != null)
                return referenceProfile.Slice;

            build.Diagnostics.Add(
                $"{analysis.Label}: the reference terrain has no profile on this section line; no cut or fill was shaded.");
            return null;
        }

        // No explicit reference: compare against this terrain's own initial triangulation — the ground as
        // it was before any modifier moved it. That is what "how much cut and fill did my grading do"
        // means, and it is the overwhelmingly common question; requiring a second terrain to ask it made
        // the feature unreachable for the case it exists to serve. An explicit reference still wins, for
        // comparing against surveyed ground that is not this terrain's own starting point.
        if (baseMesh == null)
        {
            build.Diagnostics.Add(
                $"{analysis.Label}: cut/fill shading is on but this terrain has no base triangulation to " +
                "compare against, and no reference is set.");
            return null;
        }

        TerrainSectionResult? baseSlice = cutGeometry.Slice(baseMesh, tolerance);
        if (baseSlice == null)
        {
            build.Diagnostics.Add(
                $"{analysis.Label}: the terrain's initial triangulation does not reach this section line; " +
                "no cut or fill was shaded.");
        }

        return baseSlice;
    }

    private static RhinoMesh CombineMeshes(IReadOnlyList<RhinoMesh> meshes)
    {
        var combined = new RhinoMesh();
        foreach (RhinoMesh mesh in meshes)
            combined.Append(mesh);
        RhinoGeometryConversions.NormalizeMeshInPlace(combined);
        return combined;
    }

    private static List<SectionTerrainProfile> SliceTerrainsAlongPolyline(
        TerrainBuildSnapshot snapshot,
        RhinoMesh ownerMesh,
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<Point3d> cutVertices,
        double tolerance)
    {
        var profiles = new List<SectionTerrainProfile>();
        TerrainSectionResult ownerSlice = TerrainSectionSlicer.SliceAlongPolyline(ownerMesh, cutVertices, tolerance);
        if (!ownerSlice.IsEmpty)
        {
            profiles.Add(new SectionTerrainProfile(
                snapshot.Terrain.TerrainId,
                snapshot.Terrain.Name,
                analysis.ColorArgb ?? snapshot.Terrain.TerrainColorArgb,
                ownerSlice,
                IsOwner: true));
        }

        foreach (Guid terrainId in analysis.ComparisonTerrainIds)
        {
            if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? terrain))
                continue;
            TerrainSectionResult slice = TerrainSectionSlicer.SliceAlongPolyline(terrain.Mesh, cutVertices, tolerance);
            if (slice.IsEmpty)
                continue;
            profiles.Add(new SectionTerrainProfile(
                terrain.TerrainId,
                terrain.Name,
                terrain.ColorArgb,
                slice,
                IsOwner: false));
        }

        return profiles;
    }

    private static List<SectionTerrainProfile> SampleTerrainsAlongCurve(
        TerrainBuildSnapshot snapshot,
        RhinoMesh ownerMesh,
        TerrainSectionAnnotationDefinitionBase analysis,
        Curve curve,
        double sampleInterval,
        double tolerance)
    {
        var profiles = new List<SectionTerrainProfile>();
        TerrainSectionResult ownerSlice = TerrainSectionSlicer.SampleAlongCurve(
            ownerMesh, curve, sampleInterval, tolerance);
        if (!ownerSlice.IsEmpty)
        {
            profiles.Add(new SectionTerrainProfile(
                snapshot.Terrain.TerrainId,
                snapshot.Terrain.Name,
                analysis.ColorArgb ?? snapshot.Terrain.TerrainColorArgb,
                ownerSlice,
                IsOwner: true));
        }

        foreach (Guid terrainId in analysis.ComparisonTerrainIds)
        {
            if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? terrain))
                continue;
            TerrainSectionResult slice = TerrainSectionSlicer.SampleAlongCurve(
                terrain.Mesh, curve, sampleInterval, tolerance);
            if (slice.IsEmpty)
                continue;
            profiles.Add(new SectionTerrainProfile(
                terrain.TerrainId,
                terrain.Name,
                terrain.ColorArgb,
                slice,
                IsOwner: false));
        }

        return profiles;
    }

    private static void AddMissingSectionTerrainDiagnostics(
        TerrainBuildSnapshot snapshot,
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build)
    {
        foreach (Guid terrainId in analysis.ComparisonTerrainIds.Distinct())
        {
            if (snapshot.SectionTerrains.ContainsKey(terrainId))
                continue;
            build.Diagnostics.Add(
                $"{analysis.Label}: comparison terrain {terrainId} has no completed final mesh; its profile was skipped.");
        }
    }

    /// <summary>
    /// Builds the closed boundary of a cut/fill region and returns it as a hatch. The region is a ribbon
    /// between the proposed and reference profiles, so its outline is the proposed elevations forward then
    /// the reference elevations back. Regions are already split at profile crossings by
    /// <see cref="SectionProfileComparison"/>, so the loop does not self-intersect.
    /// </summary>
    private static IReadOnlyList<Hatch> BuildComparisonRegionHatch(
        SectionComparisonRegion region,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        int hatchPatternIndex,
        double hatchScale,
        double hatchRotationDegrees,
        double tolerance)
    {
        if (region.Vertices.Count < 2)
            return Array.Empty<Hatch>();

        var loop = new List<Point3d>(region.Vertices.Count * 2 + 1);
        for (int i = 0; i < region.Vertices.Count; i++)
        {
            SectionComparisonVertex vertex = region.Vertices[i];
            AppendDistinct(loop, SectionLayoutHelper.ProjectToInsertionPlane(
                cellPlane, vertex.Station, vertex.ProposedElevation, horizontalScale, verticalScale, baseElevation), tolerance);
        }

        for (int i = region.Vertices.Count - 1; i >= 0; i--)
        {
            SectionComparisonVertex vertex = region.Vertices[i];
            AppendDistinct(loop, SectionLayoutHelper.ProjectToInsertionPlane(
                cellPlane, vertex.Station, vertex.ReferenceElevation, horizontalScale, verticalScale, baseElevation), tolerance);
        }

        // A hatch boundary needs three distinct corners; anything less encloses no area.
        if (loop.Count < 3)
            return Array.Empty<Hatch>();

        loop.Add(loop[0]);
        var boundary = new PolylineCurve(loop);
        Hatch[]? hatches = Hatch.Create(
            boundary,
            Math.Max(hatchPatternIndex, 0),
            RhinoMath.ToRadians(hatchRotationDegrees),
            hatchScale > 0.0 ? hatchScale : 1.0,
            Math.Max(tolerance, RhinoMath.ZeroTolerance));

        // Hatch.Create can split one boundary into several hatches; keeping only the first would silently
        // drop part of the filled region.
        return hatches is { Length: > 0 }
            ? hatches.Where(hatch => hatch != null).ToList()
            : (IReadOnlyList<Hatch>)Array.Empty<Hatch>();
    }

    private static void AppendDistinct(List<Point3d> points, Point3d candidate, double tolerance)
    {
        double thresholdSquared = Math.Max(tolerance, RhinoMath.ZeroTolerance);
        thresholdSquared *= thresholdSquared;
        if (points.Count > 0 && points[^1].DistanceToSquared(candidate) <= thresholdSquared)
            return;

        points.Add(candidate);
    }


    /// <summary>
    /// How the pieces of a section stack. Read bottom to top: the tint, then the grid it sits on, then the
    /// ground it was measured from, then the thing the drawing is actually about.
    /// </summary>
    /// <summary>Vertical scale for a section, defaulting to true shape when unset or nonsensical.</summary>
    private static double ResolveVerticalExaggeration(TerrainSectionAnnotationDefinitionBase analysis) =>
        analysis.VerticalExaggeration > 0.0 ? analysis.VerticalExaggeration : 1.0;

    private static class SectionDisplayOrder
    {
        public const int Fill = -3;
        public const int Grid = -2;
        public const int Existing = -1;
        public const int Proposed = 1;
    }

    private static int ResolveDisplayOrder(LayerRole role) => role switch
    {
        LayerRole.SectionsCutFillCut or LayerRole.SectionsCutFillFill => SectionDisplayOrder.Fill,
        LayerRole.SectionsGrid or LayerRole.SectionsTicks => SectionDisplayOrder.Grid,
        LayerRole.SectionsExisting => SectionDisplayOrder.Existing,
        LayerRole.Sections => SectionDisplayOrder.Proposed,
        _ => 0
    };

    /// <summary>The table to route by. Falls back to the built-in roles so a caller that has not
    /// been handed one still produces output on a real layer rather than none.</summary>
    private static LayerRoleTable Roles(LayerRoleTable? layerRoles) => layerRoles ?? LayerRoleTable.Default;

    private static GeneratedRhinoObject BuildPolylineObject(
        TerrainSectionAnnotationDefinitionBase analysis,
        Polyline polyline,
        LayerRoleTable? layerRoles,
        string name,
        LayerRole role,
        int? colorArgbOverride = null)
    {
        return new GeneratedRhinoObject
        {
            Role = role,
            Geometry = new PolylineCurve(polyline),
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = colorArgbOverride ?? analysis.ColorArgb,
            LayerPath = Roles(layerRoles).Path(role),
            DisplayOrder = ResolveDisplayOrder(role)
        };
    }

    private static GeneratedRhinoObject BuildLineObject(TerrainSectionAnnotationDefinitionBase analysis, Line line, LayerRoleTable? layerRoles, string name, LayerRole role)
    {
        return new GeneratedRhinoObject
        {
            Role = role,
            Geometry = new LineCurve(line),
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = Roles(layerRoles).Path(role),
            DisplayOrder = ResolveDisplayOrder(role)
        };
    }

    private static GeneratedRhinoObject BuildTextObject(TerrainSectionAnnotationDefinitionBase analysis, TextEntity text, LayerRoleTable? layerRoles, string name, LayerRole role)
    {
        return new GeneratedRhinoObject
        {
            Role = role,
            Geometry = text,
            Name = name,
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = Roles(layerRoles).Path(role)
        };
    }

    private static Plane ResolveInsertionPlane(TerrainSectionAnnotationDefinitionBase analysis, RhinoMesh mesh)
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
            double offset = Math.Max((bounds.Max.Y - bounds.Min.Y) * 0.25, double.Epsilon);
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

        if (stations.Count == 0 ||
            Math.Abs(stations[stations.Count - 1] - totalLength) > Math.Max(Math.Abs(totalLength) * 1e-12, double.Epsilon))
            stations.Add(totalLength);

        return stations;
    }

    private static GeneratedRhinoObject CreateAnnotationObject(
        TerrainBuildSnapshot snapshot,
        BlockAttributeAnnotationDefinition analysis,
        int index,
        Point3d worldPoint,
        double rawValue,
        string unitSuffix,
        double? distance,
        string? blockDefinitionName,
        MarkerBlockTemplate template,
        LayerRoleTable? layerRoles = null,
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
            Role = LayerRole.Markers,
            Name = $"{analysis.Label} {index}",
            AnalysisId = analysis.Id,
            ColorArgb = analysis.ColorArgb,
            LayerPath = Roles(layerRoles).Path(LayerRole.Markers),
            InstanceDefinitionName = string.IsNullOrWhiteSpace(blockDefinitionName)
                ? GeneratedBlockCatalog.GetDefaultDefinitionName(template)
                : blockDefinitionName,
            MarkerBlockTemplate = template,
            InstanceUserStrings = userStrings,
            InstanceTransform = CreateInstanceTransform(
                worldPoint,
                ResolveBlockScale(snapshot, analysis.FollowsAnnotationStyle, analysis.BlockScale),
                direction)
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

    /// <summary>
    /// Text size for generated section annotation. Following the annotation style means size is governed by
    /// the Rhino dimension style the user edits, not by a value stored in the definition; migrated
    /// documents keep their stored absolute height.
    /// </summary>
    private static double ResolveTextHeight(
        TerrainBuildSnapshot snapshot,
        TerrainSectionAnnotationDefinitionBase analysis)
    {
        return analysis.FollowsAnnotationStyle
            ? snapshot.AnnotationStyle.TextHeight
            : analysis.TextHeight;
    }

    /// <summary>
    /// Symbol size for a marker block. When the owner follows the annotation style, the stored scale is a
    /// multiplier on the size derived from the style's effective text height, so symbols stay in step with
    /// label text whenever the style is edited in Rhino. Otherwise the stored value is an absolute scale
    /// (the pre-schema-27 behaviour, preserved for migrated documents).
    /// </summary>
    internal static double ResolveBlockScale(TerrainBuildSnapshot snapshot, bool followsStyle, double storedScale)
    {
        double scale = followsStyle
            ? snapshot.AnnotationStyle.GetBlockScale(storedScale)
            : storedScale;
        return Math.Max(scale, 0.01);
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

    private static bool TryGetTerrainSlopeNormal(RhinoMesh mesh, int faceIndex, out Vector3d normal)
    {
        normal = Vector3d.Unset;
        if (faceIndex >= 0 && faceIndex < mesh.FaceNormals.Count)
        {
            Vector3d faceNormal = mesh.FaceNormals[faceIndex];
            if (faceNormal.IsValid && faceNormal.Unitize())
            {
                normal = faceNormal;
                return true;
            }
        }

        return false;
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

    private static double MinimumLength(TerrainBuildSnapshot snapshot) =>
        Math.Max(snapshot.ModelAbsoluteTolerance, snapshot.ResolvedUnitContext.FromMeters(1e-9));

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
