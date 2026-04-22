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
        Func<bool>? shouldCancel)
    {
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
                var previousMeshPoint = mesh.ClosestMeshPoint(previous.Point, 0.0);
                var currentMeshPoint = mesh.ClosestMeshPoint(current.Point, 0.0);
                if (previousMeshPoint == null || currentMeshPoint == null)
                {
                    cumulativeDistance += Math.Max(0.0, curve.GetLength(new Interval(previous.Parameter, current.Parameter)));
                    continue;
                }

                Point3d previousWorld = mesh.PointAt(previousMeshPoint);
                Point3d currentWorld = mesh.PointAt(currentMeshPoint);
                double horizontalRun = Math.Sqrt(
                    ((currentWorld.X - previousWorld.X) * (currentWorld.X - previousWorld.X)) +
                    ((currentWorld.Y - previousWorld.Y) * (currentWorld.Y - previousWorld.Y)));
                double segmentLength = Math.Max(0.0, curve.GetLength(new Interval(previous.Parameter, current.Parameter)));
                if (horizontalRun <= snapshot.ModelAbsoluteTolerance)
                {
                    cumulativeDistance += segmentLength;
                    continue;
                }

                double slopeRatio = Math.Abs(currentWorld.Z - previousWorld.Z) / horizontalRun;
                double slopeValue = SlopeAnalyzer.ConvertRatioToUnit(slopeRatio, analysis.Unit);
                stats.Add(slopeValue);
                outputCount++;

                if (analysis.IsEnabled)
                {
                    Point3d labelPoint = Midpoint(previousWorld, currentWorld);
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
                        GetCurveSlopeDirection(previousWorld, currentWorld, snapshot.ModelAbsoluteTolerance, analysis.FlipDirection)));
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
        Func<bool>? shouldCancel)
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
                var meshPoint = mesh.ClosestMeshPoint(current.Point, 0.0);
                if (meshPoint != null)
                {
                    Point3d worldPoint = mesh.PointAt(meshPoint);
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
                            MarkerBlockTemplate.AnnotationElevation));
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
        Func<bool>? shouldCancel)
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
                var meshPoint = mesh.ClosestMeshPoint(samplePoint, 0.0);
                if (meshPoint == null)
                    continue;

                Point3d worldPoint = mesh.PointAt(meshPoint);
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
                    MarkerBlockTemplate.AnnotationElevation));
            }
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
    }

    public static TerrainAnalysisSummary BuildPointSlopeSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        PointSlopeLabelAnalysisDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        mesh.Normals.ComputeNormals();
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
            var meshPoint = mesh.ClosestMeshPoint(point.Location, 0.0);
            if (meshPoint == null)
                continue;

            Point3d worldPoint = mesh.PointAt(meshPoint);
            Vector3d normal = mesh.NormalAt(meshPoint);
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
                direction));
        }

        return CreateSummary(analysis.Id, sourceCount, outputCount, stats);
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
            LayerPath = analysis.OutputLayerPath,
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

    private static Vector3d GetCurveSlopeDirection(Point3d a, Point3d b, double tolerance, bool flip)
    {
        Vector3d direction = b.Z <= a.Z + tolerance
            ? b - a
            : a - b;
        direction.Z = 0.0;
        if (flip)
            direction = -direction;
        return direction.Length <= tolerance
            ? Vector3d.Unset
            : direction;
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
