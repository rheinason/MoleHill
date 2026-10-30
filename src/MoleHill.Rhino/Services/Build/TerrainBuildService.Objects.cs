using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Marker and object-placement stages: durable base/footprint solving, absolute variation, and marker block templates.
internal sealed partial class TerrainBuildService
{
    private static void BuildMarkers(TerrainBuildSnapshot snapshot, TerrainDefinition terrain, RhinoMesh mesh, TerrainBuildResult build, Func<bool>? shouldCancel)
    {
        int enabledMarkerCount = terrain.Markers.Count(marker => marker.IsEnabled);
        if (enabledMarkerCount == 0)
            return;

        mesh.Normals.ComputeNormals();

        foreach (var marker in terrain.Markers.Where(marker => marker.IsEnabled))
        {
            ThrowIfCancellationRequested(shouldCancel);
            var samplePoints = TerrainBuildSnapshotResolver.ResolveMarkerSamplePoints(snapshot, marker.Sources);
            for (int sampleIndex = 0; sampleIndex < samplePoints.Count; sampleIndex++)
            {
                if ((sampleIndex & 31) == 0)
                    ThrowIfCancellationRequested(shouldCancel);

                var samplePoint = samplePoints[sampleIndex];
                if (!TryResolveTerrainPoint(snapshot, mesh, samplePoint, out Point3d worldPoint, out Vector3d normal, out _))
                    continue;

                string text;

                switch (marker)
                {
                    case ElevationMarkerDefinition elevation:
                        text = worldPoint.Z.ToString(elevation.Format);
                        break;
                    case SlopeMarkerDefinition slope:
                        double slopeRadians = Math.Atan2(Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y), Math.Abs(normal.Z));
                        double value = slope.AsPercent
                            ? Math.Tan(slopeRadians) * 100.0
                            : slopeRadians * 180.0 / Math.PI;
                        text = value.ToString(slope.Format) + (slope.AsPercent ? "%" : "deg");
                        break;
                    default:
                        continue;
                }

                if (marker.UseBlockInstance)
                {
                    build.MarkerObjects.Add(new GeneratedRhinoObject
                    {
                        Role = LayerRole.Markers,
                        Name = marker.Name,
                        LayerPath = snapshot.LayerRoles.Path(LayerRole.Markers),
                        InstanceDefinitionName = GetMarkerBlockName(marker),
                        MarkerBlockTemplate = GetMarkerBlockTemplate(marker),
                        InstanceTransform = Transform.Translation(worldPoint - Point3d.Origin)
                            * Transform.Scale(
                                Point3d.Origin,
                                TerrainAnalysisAnnotationBuilder.ResolveBlockScale(
                                    snapshot,
                                    marker.FollowsAnnotationStyle,
                                    marker.BlockScale,
                                    GetMarkerBlockName(marker))),
                        ColorArgb = marker.ColorArgb
                    });
                }

                if (!marker.ShowValueLabel)
                    continue;

                build.MarkerObjects.Add(new GeneratedRhinoObject
                {
                    Role = LayerRole.MarkerLabels,
                    Geometry = new TextDot(text, worldPoint),
                    Name = marker.Name,
                    LayerPath = snapshot.LayerRoles.Path(LayerRole.MarkerLabels),
                    ColorArgb = marker.ColorArgb
                });
            }
        }
    }

    private static void BuildObjectPlacements(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        var placeableDefinitions = terrain.Objects
            .Where(item => item.IsEnabled && item is not ScatterObjectDefinition)
            .ToList();
        if (placeableDefinitions.Count == 0)
            return;

        mesh.Normals.ComputeNormals();

        var resolvedEntries = placeableDefinitions
            .Select(item => (Definition: item, Objects: TerrainBuildSnapshotResolver.ResolveObjects(snapshot, item.Sources)
                .Where(obj => !item.ReleasedObjectIds.Contains(obj.ObjectId)).ToArray()))
            .ToList();

        var owners = new Dictionary<Guid, Guid>();
        var overlappingObjectIds = new HashSet<Guid>();
        foreach (var entry in resolvedEntries)
        {
            foreach (var obj in entry.Objects)
            {
                if (!owners.TryAdd(obj.ObjectId, entry.Definition.Id))
                    overlappingObjectIds.Add(obj.ObjectId);
            }
        }

        foreach (var objectId in overlappingObjectIds.OrderBy(id => id))
            build.Diagnostics.Add($"Objects skipped {FormatObjectRef(objectId)} because it is matched by multiple object definitions.");

        foreach (var entry in resolvedEntries)
        {
            ThrowIfCancellationRequested(shouldCancel);

            var stateByObjectId = entry.Definition.PlacementStates
                .Where(state => state.ObjectId != Guid.Empty)
                .GroupBy(state => state.ObjectId)
                .ToDictionary(group => group.Key, group => group.Last());

            var placementGroup = new TerrainObjectPlacementGroup
            {
                DefinitionId = entry.Definition.Id
            };

            foreach (var obj in entry.Objects)
            {
                ThrowIfCancellationRequested(shouldCancel);

                if (overlappingObjectIds.Contains(obj.ObjectId))
                    continue;

                if (!TryBuildObjectPlacement(snapshot, entry.Definition, obj, stateByObjectId, mesh, out var placement, out string? diagnostic))
                {
                    if (!string.IsNullOrWhiteSpace(diagnostic))
                        build.Diagnostics.Add(diagnostic);
                    continue;
                }

                placementGroup.Placements.Add(placement);
            }

            if (placementGroup.Placements.Count == 0)
                continue;

            build.ObjectPlacements.Add(placementGroup);
        }
    }

    internal static bool TryBuildObjectPlacement(
        TerrainBuildSnapshot snapshot,
        TerrainObjectDefinition definition,
        ResolvedSourceObject obj,
        IReadOnlyDictionary<Guid, TerrainObjectPlacementState> stateByObjectId,
        RhinoMesh mesh,
        out TerrainObjectPlacement placement,
        out string? diagnostic)
    {
        placement = new TerrainObjectPlacement();
        diagnostic = null;

        using GeometryBase baselineGeometry = obj.Geometry.Duplicate();
        Transform previousTransform = stateByObjectId.TryGetValue(obj.ObjectId, out var existingState)
            ? existingState.GetLastAppliedTransform()
            : Transform.Identity;
        if ((existingState != null && (existingState.LastAppliedTransform is not { Length: 16 } ||
             existingState.LastAppliedTransform.Any(value => !double.IsFinite(value)))) ||
            !TryGetInverse(previousTransform, out Transform inversePrevious))
        {
            diagnostic = $"Objects skipped {FormatObjectRef(obj.ObjectId)} because its stored placement transform could not be inverted.";
            return false;
        }

        if (!TryRemoveAppliedTransform(baselineGeometry, previousTransform))
        {
            diagnostic = $"Objects skipped {FormatObjectRef(obj.ObjectId)} because its stored placement transform could not be inverted.";
            return false;
        }

        BoundingBox baselineBoundingBox = baselineGeometry.GetBoundingBox(true);
        if (!baselineBoundingBox.IsValid && obj.WorldBoundingBox.IsValid)
            baselineBoundingBox = TransformBoundingBox(obj.WorldBoundingBox, inversePrevious);

        bool hasReference = ObjectPlacementGeometry.TryGetReference(obj, baselineGeometry, inversePrevious, out var reference);
        if (hasReference && obj.HasSourceTransform)
            baselineBoundingBox = new BoundingBox(reference.BoxCorners);

        Transform appliedTransform;
        switch (definition)
        {
            case LowestPointObjectDefinition:
                if (!TryCreateLowestPointPlacement(snapshot, definition, obj.ObjectId, baselineGeometry, baselineBoundingBox, mesh, out appliedTransform, out diagnostic))
                    return false;
                break;
            case SurfaceOrientedObjectDefinition:
                if (!hasReference || !TryCreateFootprintPlacement(snapshot, definition, obj.ObjectId, reference, mesh, out appliedTransform, out diagnostic))
                {
                    diagnostic ??= $"Objects skipped {FormatObjectRef(obj.ObjectId)} because its base could not be resolved. Use Change Base.";
                    return false;
                }
                break;
            default:
                diagnostic = $"Objects skipped {FormatObjectRef(obj.ObjectId)} because its object definition type is unsupported.";
                return false;
        }

        placement = new TerrainObjectPlacement
        {
            ObjectId = obj.ObjectId,
            AppliedTransform = appliedTransform,
            ExpectedPreviousTransform = previousTransform,
            BaselineReference = hasReference ? reference : null,
            SourceGeometryCrc = obj.GeometryDataCrc,
            SourceReferenceFingerprint = ObjectPlacementGeometry.ReferenceFingerprint(obj.Geometry)
        };
        diagnostic = null;
        return true;
    }

    private static bool TryCreateLowestPointPlacement(
        TerrainBuildSnapshot snapshot,
        TerrainObjectDefinition definition,
        Guid objectId,
        GeometryBase geometry,
        BoundingBox fallbackBoundingBox,
        RhinoMesh mesh,
        out Transform appliedTransform,
        out string? diagnostic)
    {
        appliedTransform = Transform.Identity;
        diagnostic = null;

        if (!TryGetLowestPoint(geometry, fallbackBoundingBox, out var lowestPoint))
        {
            diagnostic = $"Objects skipped {FormatObjectRef(objectId)} because its lowest point could not be resolved.";
            return false;
        }

        Transform variation = CreateRandomPlacementTransform(definition, objectId, lowestPoint, Vector3d.ZAxis);
        using GeometryBase variedGeometry = geometry.Duplicate();
        if (!TryApplyTransform(variedGeometry, variation) ||
            !TryGetLowestPoint(variedGeometry, TransformBoundingBox(fallbackBoundingBox, variation), out var variedLowest))
            return false;
        if (!TryResolveTerrainPoint(snapshot, mesh, variedLowest, out Point3d terrainPoint, out _, out diagnostic))
            return false;

        Point3d targetPoint = terrainPoint + (Vector3d.ZAxis * definition.ZOffset);
        Transform basePlacement = Transform.Translation(targetPoint - variedLowest);
        appliedTransform = basePlacement * variation;
        return true;
    }

    private static bool TryCreateFootprintPlacement(
        TerrainBuildSnapshot snapshot, TerrainObjectDefinition definition, Guid objectId,
        ObjectPlacementReference reference, RhinoMesh mesh, out Transform appliedTransform, out string? diagnostic)
    {
        appliedTransform = Transform.Identity;
        diagnostic = null;
        if (!reference.TryGetPlane(out var source)) return false;
        Vector3d heading = ProjectToWorldHorizontal(source.XAxis);
        if (!heading.Unitize())
        {
            heading = ProjectToWorldHorizontal(source.YAxis);
            if (!heading.Unitize()) return false;
        }
        var horizontal = new Plane(source.Origin, heading, Vector3d.CrossProduct(Vector3d.ZAxis, heading));
        Transform variation = CreateRandomPlacementTransform(definition, objectId, source.Origin, Vector3d.ZAxis);
        Transform flatten = variation * Transform.PlaneToPlane(source, horizontal);
        heading.Transform(variation);
        if (!heading.Unitize()) return false;
        var hits = new List<Point3d>(4);
        foreach (var corner in reference.BaseCorners)
        {
            Point3d sample = corner;
            sample.Transform(flatten);
            if (!TryResolveTerrainPoint(snapshot, mesh, sample, out Point3d hit, out _, out _))
            {
                diagnostic = $"Objects skipped {FormatObjectRef(objectId)} because its footprint extends outside the terrain.";
                return false;
            }
            hits.Add(hit);
        }
        if (!ObjectPlacementGeometry.TryFitTerrainFrame(hits, heading, out var target))
        {
            diagnostic = $"Objects skipped {FormatObjectRef(objectId)} because its footprint does not define a stable terrain plane.";
            return false;
        }
        target.Origin += target.Normal * definition.ZOffset;
        var variedHorizontal = new Plane(source.Origin, heading, Vector3d.CrossProduct(Vector3d.ZAxis, heading));
        appliedTransform = Transform.PlaneToPlane(variedHorizontal, target) * flatten;
        return appliedTransform.IsValid;
    }

    private static bool TryResolveTerrainPoint(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        Point3d samplePoint,
        out Point3d terrainPoint,
        out Vector3d terrainNormal,
        out string? diagnostic)
    {
        terrainPoint = Point3d.Unset;
        terrainNormal = Vector3d.Unset;
        diagnostic = null;

        if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(
                mesh,
                samplePoint,
                Math.Max(snapshot.ModelAbsoluteTolerance, ModelUnits.FromMeters(1e-4, snapshot.ModelUnitSystem)),
                out terrainPoint,
                out MeshPoint? meshPoint))
        {
            diagnostic = "Objects skipped a source object because it is outside the terrain footprint.";
            return false;
        }

        if (meshPoint == null)
        {
            diagnostic = "Objects skipped a source object because no terrain sample point was found.";
            return false;
        }

        terrainNormal = mesh.NormalAt(meshPoint);
        if (!terrainNormal.Unitize())
        {
            diagnostic = "Objects skipped a source object because the terrain normal is invalid at the sample point.";
            return false;
        }

        return true;
    }

    private static bool TryCreateTerrainFrame(Point3d origin, Vector3d terrainNormal, Vector3d preferredXAxis, out Plane plane)
    {
        plane = Plane.Unset;
        if (!terrainNormal.Unitize())
            return false;

        Vector3d xAxis = preferredXAxis - (terrainNormal * Vector3d.Multiply(preferredXAxis, terrainNormal));
        if (!xAxis.Unitize())
        {
            xAxis = Vector3d.XAxis - (terrainNormal * Vector3d.Multiply(Vector3d.XAxis, terrainNormal));
            if (!xAxis.Unitize())
            {
                xAxis = Vector3d.YAxis - (terrainNormal * Vector3d.Multiply(Vector3d.YAxis, terrainNormal));
                if (!xAxis.Unitize())
                    return false;
            }
        }

        Vector3d yAxis = Vector3d.CrossProduct(terrainNormal, xAxis);
        if (!yAxis.Unitize())
            return false;

        plane = new Plane(origin, xAxis, yAxis);
        return plane.IsValid;
    }

    private static Transform CreateRandomPlacementTransform(
        TerrainObjectDefinition definition,
        Guid objectId,
        Point3d anchor,
        Vector3d axis)
    {
        Transform scaleTransform = Transform.Identity;
        double scale = definition.PlacementScale * SampleDeterministicRange(
            definition.Id,
            objectId,
            definition.RandomSeed,
            0,
            definition.RandomScaleMin,
            definition.RandomScaleMax,
            1.0);
        if (Math.Abs(scale - 1.0) > 1e-9)
            scaleTransform = Transform.Scale(anchor, scale);

        Transform rotationTransform = Transform.Identity;
        double rotationDegrees = definition.HeadingDegrees + SampleDeterministicRange(
            definition.Id,
            objectId,
            definition.RandomSeed,
            1,
            definition.RandomRotationMinDegrees,
            definition.RandomRotationMaxDegrees,
            0.0);
        if (Math.Abs(rotationDegrees) > 1e-9)
        {
            Vector3d rotationAxis = axis;
            if (!rotationAxis.Unitize())
                rotationAxis = Vector3d.ZAxis;

            rotationTransform = Transform.Rotation(RhinoMath.ToRadians(rotationDegrees), rotationAxis, anchor);
        }

        return rotationTransform * scaleTransform;
    }

    private static double SampleDeterministicRange(
        Guid definitionId,
        Guid objectId,
        int seed,
        int channel,
        double min,
        double max,
        double fallbackValue)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
            return fallbackValue;

        if (max < min)
            (min, max) = (max, min);

        if (Math.Abs(max - min) <= 1e-9)
            return min;

        double unit = SampleDeterministicUnit(definitionId, objectId, seed, channel);
        return min + ((max - min) * unit);
    }

    private static double SampleDeterministicUnit(Guid definitionId, Guid objectId, int seed, int channel)
    {
        byte[] buffer = new byte[40];
        definitionId.ToByteArray().CopyTo(buffer, 0);
        objectId.ToByteArray().CopyTo(buffer, 16);
        BitConverter.TryWriteBytes(buffer.AsSpan(32, 4), seed);
        BitConverter.TryWriteBytes(buffer.AsSpan(36, 4), channel);

        ulong hash = 14695981039346656037UL;
        foreach (byte value in buffer)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        const double divisor = 1UL << 53;
        ulong mantissa = hash >> 11;
        return mantissa / divisor;
    }

    private static BoundingBox TransformBoundingBox(BoundingBox bbox, Transform transform)
    {
        if (!bbox.IsValid)
            return BoundingBox.Empty;

        Point3d[] corners = bbox.GetCorners();
        for (int index = 0; index < corners.Length; index++)
            corners[index].Transform(transform);

        return new BoundingBox(corners);
    }

    private static Vector3d ProjectToWorldHorizontal(Vector3d axis)
    {
        return new Vector3d(axis.X, axis.Y, 0.0);
    }

    private static bool TryRemoveAppliedTransform(GeometryBase geometry, Transform appliedTransform)
    {
        if (IsIdentityTransform(appliedTransform))
            return true;

        if (!TryGetInverse(appliedTransform, out Transform inverse))
            return false;

        return TryApplyTransform(geometry, inverse);
    }

    private static bool TryApplyTransform(GeometryBase geometry, Transform transform)
    {
        if (IsIdentityTransform(transform))
            return true;

        return geometry.Transform(transform);
    }

    private static bool TryGetInverse(Transform transform, out Transform inverse)
    {
        if (IsIdentityTransform(transform))
        {
            inverse = Transform.Identity;
            return true;
        }

        return transform.TryGetInverse(out inverse);
    }

    private static bool IsIdentityTransform(Transform transform, double tolerance = 1e-9)
    {
        return Math.Abs(transform.M00 - 1.0) <= tolerance &&
               Math.Abs(transform.M01) <= tolerance &&
               Math.Abs(transform.M02) <= tolerance &&
               Math.Abs(transform.M03) <= tolerance &&
               Math.Abs(transform.M10) <= tolerance &&
               Math.Abs(transform.M11 - 1.0) <= tolerance &&
               Math.Abs(transform.M12) <= tolerance &&
               Math.Abs(transform.M13) <= tolerance &&
               Math.Abs(transform.M20) <= tolerance &&
               Math.Abs(transform.M21) <= tolerance &&
               Math.Abs(transform.M22 - 1.0) <= tolerance &&
               Math.Abs(transform.M23) <= tolerance &&
               Math.Abs(transform.M30) <= tolerance &&
               Math.Abs(transform.M31) <= tolerance &&
               Math.Abs(transform.M32) <= tolerance &&
               Math.Abs(transform.M33 - 1.0) <= tolerance;
    }

    private static bool TryGetLowestPoint(GeometryBase geometry, BoundingBox fallbackBoundingBox, out Point3d point)
    {
        point = Point3d.Unset;

        switch (geometry)
        {
            case Point rhinoPoint:
                point = rhinoPoint.Location;
                return true;
            case PointCloud pointCloud when pointCloud.Count > 0:
                point = Enumerable.Range(0, pointCloud.Count)
                    .Select(index => pointCloud[index].Location)
                    .OrderBy(candidate => candidate.Z)
                    .First();
                return true;
            case Curve curve:
                point = GetLowestCurvePoint(curve);
                return point.IsValid;
            case Mesh mesh when TryGetLowestMeshPoint(mesh, out point):
                return true;
            case Brep brep:
                return TryGetLowestPointFromMeshes(Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>(), out point);
            case Extrusion extrusion:
                var extrusionBrep = extrusion.ToBrep();
                if (extrusionBrep == null)
                    break;

                return TryGetLowestPointFromMeshes(Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>(), out point);
            case InstanceReferenceGeometry when fallbackBoundingBox.IsValid:
                point = new Point3d(
                    (fallbackBoundingBox.Min.X + fallbackBoundingBox.Max.X) * 0.5,
                    (fallbackBoundingBox.Min.Y + fallbackBoundingBox.Max.Y) * 0.5,
                    fallbackBoundingBox.Min.Z);
                return true;
        }

        BoundingBox bbox = geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            bbox = fallbackBoundingBox;
        if (!bbox.IsValid)
            return false;

        point = new Point3d(
            (bbox.Min.X + bbox.Max.X) * 0.5,
            (bbox.Min.Y + bbox.Max.Y) * 0.5,
            bbox.Min.Z);
        return true;
    }

    private static Point3d GetLowestCurvePoint(Curve curve)
    {
        var candidates = new List<Point3d> { curve.PointAtStart, curve.PointAtEnd };
        var parameters = curve.DivideByCount(64, true);
        if (parameters != null)
        {
            foreach (double parameter in parameters)
                candidates.Add(curve.PointAt(parameter));
        }

        return candidates
            .Where(candidate => candidate.IsValid)
            .OrderBy(candidate => candidate.Z)
            .FirstOrDefault();
    }

    private static bool TryGetLowestPointFromMeshes(IEnumerable<Mesh> meshes, out Point3d point)
    {
        point = Point3d.Unset;
        bool found = false;
        foreach (var mesh in meshes)
        {
            if (!TryGetLowestMeshPoint(mesh, out Point3d candidate))
                continue;

            if (!found || candidate.Z < point.Z)
            {
                point = candidate;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetLowestMeshPoint(Mesh mesh, out Point3d point)
    {
        point = Point3d.Unset;
        if (mesh.Vertices.Count == 0)
            return false;

        var lowest = mesh.Vertices[0];
        for (int index = 1; index < mesh.Vertices.Count; index++)
        {
            var candidate = mesh.Vertices[index];
            if (candidate.Z < lowest.Z)
                lowest = candidate;
        }

        point = new Point3d(lowest.X, lowest.Y, lowest.Z);
        return true;
    }

    private static string FormatObjectRef(Guid objectId)
    {
        return objectId == Guid.Empty
            ? "object"
            : objectId.ToString("N")[..8];
    }

    private static MarkerBlockTemplate GetMarkerBlockTemplate(MarkerDefinition marker)
    {
        return marker switch
        {
            ElevationMarkerDefinition => MarkerBlockTemplate.Elevation,
            SlopeMarkerDefinition => MarkerBlockTemplate.Slope,
            _ => MarkerBlockTemplate.None
        };
    }

    private static string GetMarkerBlockName(MarkerDefinition marker)
    {
        if (!string.IsNullOrWhiteSpace(marker.BlockDefinitionName))
            return marker.BlockDefinitionName!;

        return marker switch
        {
            ElevationMarkerDefinition => "MoleHill_ElevationMarker",
            SlopeMarkerDefinition => "MoleHill_SlopeMarker",
            _ => "MoleHill_Marker"
        };
    }
}
