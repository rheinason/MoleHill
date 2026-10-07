using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Helpers shared by every annotation builder: object construction, layer-role stamping, sampling,
// text sizing and formatting.
internal static partial class TerrainAnalysisAnnotationBuilder
{
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
