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
    /// <summary>
    /// Elevation labels for a Spot Heights card: at points (and curve edit points), at stations along
    /// curves, or on a plan grid inside optional boundaries.
    /// </summary>
    public static TerrainAnalysisSummary BuildSpotHeightsSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SpotHeightsAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
    {
        var stats = new ValueStats();
        int outputCount = 0;

        void Emit(Point3d worldPoint, double? distance)
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
                    distance,
                    analysis.BlockDefinitionName,
                    MarkerBlockTemplate.AnnotationElevation,
                    layerRoles));
            }
        }

        int sourceCount = analysis.Placement switch
        {
            SpotPlacement.Curves => VisitCurveStations(snapshot, mesh, analysis, shouldCancel, station =>
            {
                if (TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, station.Point, snapshot.ModelAbsoluteTolerance, out Point3d worldPoint))
                    Emit(worldPoint, station.Distance);
            }),
            SpotPlacement.Grid => VisitGridNodes(snapshot, mesh, analysis, shouldCancel, (worldPoint, _) => Emit(worldPoint, null)),
            _ => VisitPointSamples(snapshot, mesh, analysis, shouldCancel, (worldPoint, _) => Emit(worldPoint, null)),
        };

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    /// <summary>
    /// Slope labels with a downhill arrow for a Spot Slope card. Along curves a label sits at the middle
    /// of each span between stations; on a grid a flat node has no downhill direction and gets no arrow.
    /// Slope and arrow always come from the terrain normal under the label, never from a curve's own Z.
    /// </summary>
    public static TerrainAnalysisSummary BuildSpotSlopeSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SpotSlopeAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null)
    {
        mesh.FaceNormals.ComputeFaceNormals();
        double tolerance = snapshot.ModelAbsoluteTolerance;
        var stats = new ValueStats();
        int outputCount = 0;
        string unitSuffix = GetSlopeUnitSuffix(analysis.Unit);

        void Emit(Point3d worldPoint, int faceIndex, double? distance, bool skipFlat)
        {
            if (!TryGetTerrainSlopeNormal(mesh, faceIndex, out Vector3d normal))
                return;

            Vector3d direction = GetTerrainSlopeDirection(normal, tolerance, analysis.FlipDirection);
            if (skipFlat && !direction.IsValid)
                return; // flat grid node: no meaningful downhill aspect, so no arrow

            double slopeRadians = Math.Atan2(Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y)), Math.Abs(normal.Z));
            double slopeValue = SlopeAnalyzer.ConvertRatioToUnit(Math.Tan(slopeRadians), analysis.Unit);

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
                    distance,
                    analysis.BlockDefinitionName,
                    MarkerBlockTemplate.AnnotationSlope,
                    layerRoles,
                    direction));
            }
        }

        int sourceCount = analysis.Placement switch
        {
            SpotPlacement.Curves => VisitCurveSpans(snapshot, mesh, analysis, shouldCancel, (midXy, distance) =>
            {
                if (TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, midXy, tolerance, out Point3d labelPoint, out int faceIndex))
                    Emit(labelPoint, faceIndex, distance, skipFlat: false);
            }),
            SpotPlacement.Grid => VisitGridNodes(snapshot, mesh, analysis, shouldCancel,
                (worldPoint, faceIndex) => Emit(worldPoint, faceIndex, null, skipFlat: true)),
            _ => VisitPointSamples(snapshot, mesh, analysis, shouldCancel,
                (worldPoint, faceIndex) => Emit(worldPoint, faceIndex, null, skipFlat: false)),
        };

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    private readonly record struct CurveStation(Point3d Point, double Distance);

    /// <summary>
    /// Points placement: every picked point, and every edit point of a picked curve, projected onto the
    /// terrain. Returns the number of point and curve sources.
    /// </summary>
    private static int VisitPointSamples(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SpotLabelAnnotationDefinition analysis,
        Func<bool>? shouldCancel,
        Action<Point3d, int> visit)
    {
        int sourceCount = 0;
        foreach (var obj in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources))
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (obj.Geometry is not Point && obj.Geometry is not Curve)
                continue;

            sourceCount++;
            foreach (var samplePoint in ExtractElevationSamples(obj.Geometry, snapshot.ModelAbsoluteTolerance))
            {
                if (TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, samplePoint, snapshot.ModelAbsoluteTolerance, out Point3d worldPoint, out int faceIndex))
                    visit(worldPoint, faceIndex);
            }
        }

        return sourceCount;
    }

    /// <summary>
    /// Curves placement, at the stations themselves (elevation): each station with its distance along the
    /// curve. Returns the number of curve sources.
    /// </summary>
    private static int VisitCurveStations(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SpotLabelAnnotationDefinition analysis,
        Func<bool>? shouldCancel,
        Action<CurveStation> visit)
    {
        int sourceCount = 0;
        foreach (var obj in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.CurveSources))
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (obj.Geometry is not Curve curve)
                continue;

            sourceCount++;
            var divisions = GetCurveDivisionSamples(curve, Math.Max(analysis.Interval, MinimumLength(snapshot)));
            double cumulativeDistance = 0.0;
            for (int sampleIndex = 0; sampleIndex < divisions.Count; sampleIndex++)
            {
                if ((sampleIndex & 31) == 0)
                    ThrowIfCancellationRequested(shouldCancel);

                var current = divisions[sampleIndex];
                visit(new CurveStation(current.Point, cumulativeDistance));

                if (sampleIndex + 1 < divisions.Count)
                    cumulativeDistance += Math.Max(0.0, curve.GetLength(new Interval(current.Parameter, divisions[sampleIndex + 1].Parameter)));
            }
        }

        return sourceCount;
    }

    /// <summary>
    /// Curves placement, between the stations (slope): the plan midpoint of each span with the distance
    /// along the curve to it. Returns the number of curve sources.
    /// </summary>
    private static int VisitCurveSpans(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SpotLabelAnnotationDefinition analysis,
        Func<bool>? shouldCancel,
        Action<Point3d, double> visit)
    {
        int sourceCount = 0;
        foreach (var obj in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.CurveSources))
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (obj.Geometry is not Curve curve)
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
                visit(Midpoint(previous.Point, current.Point), cumulativeDistance + (segmentLength * 0.5));
                cumulativeDistance += segmentLength;
            }
        }

        return sourceCount;
    }

    /// <summary>
    /// Grid placement: nodes of a plan grid over the terrain's extent, kept inside any closed boundary,
    /// projected onto the terrain. Returns the number of boundaries.
    /// </summary>
    private static int VisitGridNodes(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        SpotLabelAnnotationDefinition analysis,
        Func<bool>? shouldCancel,
        Action<Point3d, int> visit)
    {
        double tolerance = snapshot.ModelAbsoluteTolerance;
        var boundaries = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, analysis.Boundaries);
        var bounds = mesh.GetBoundingBox(true);
        if (!bounds.IsValid)
            return boundaries.Count;

        double spacing = Math.Max(analysis.GridSpacing, tolerance * 10.0);
        for (double y = bounds.Min.Y; y <= bounds.Max.Y + (spacing * 0.5); y += spacing)
        {
            ThrowIfCancellationRequested(shouldCancel);
            for (double x = bounds.Min.X; x <= bounds.Max.X + (spacing * 0.5); x += spacing)
            {
                var sampleXy = new Point3d(x, y, 0.0);
                if (boundaries.Count > 0 && !IsInsideAnyBoundary(sampleXy, boundaries, tolerance))
                    continue;

                if (TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, sampleXy, tolerance, out Point3d worldPoint, out int faceIndex))
                    visit(worldPoint, faceIndex);
            }
        }

        return boundaries.Count;
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
