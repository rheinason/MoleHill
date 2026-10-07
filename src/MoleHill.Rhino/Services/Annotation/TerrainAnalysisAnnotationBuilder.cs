using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Point and curve labels, slope arrows and grade callouts. The section family lives in
// TerrainAnalysisAnnotationBuilder.Sections.cs; the emit helpers every annotation shares in
// TerrainAnalysisAnnotationBuilder.Emit.cs.
internal static partial class TerrainAnalysisAnnotationBuilder
{
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
}
