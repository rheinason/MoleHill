using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Validated project-local/real-world transform storage with safe Python FOTM and legacy C# migration.
internal enum LegacyProjectBaseConvention
{
    WorldToLocal,
    LocalToWorld
}

internal readonly record struct LegacyProjectBaseCandidate(
    string Name,
    Plane Plane,
    LegacyProjectBaseConvention Convention);

internal static class ProjectBaseCPlaneService
{
    public const string ProjectBaseCPlaneName = "MoleHill_ProjectBase";
    public const string LegacyProjectBaseCPlaneName = "Georef";
    public const string LegacyFotmCPlaneName = "FOTM";

    private const string DocumentStringSection = "MoleHill.Rhino";
    private const string LegacyFallbackDisabledEntry = "ProjectBase.LegacyFallbackDisabled";
    private const double AxisTolerance = 1e-9;

    public static bool HasProjectBasePlane(RhinoDoc doc)
    {
        return doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneName) >= 0;
    }

    public static bool TryGetProjectBasePlane(RhinoDoc doc, out Plane plane, out string? error)
    {
        plane = Plane.WorldXY;
        error = null;
        int index = doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneName);
        if (index < 0)
            return false;

        plane = doc.NamedConstructionPlanes[index].Plane;
        if (TryValidateProjectBasePlane(plane, out error))
            return true;

        error = $"Named CPlane '{ProjectBaseCPlaneName}' is not a valid horizontal MoleHill project base. {error}";
        return false;
    }

    public static bool TryGetLegacyCandidate(RhinoDoc doc, out LegacyProjectBaseCandidate candidate)
    {
        candidate = default;
        if (IsLegacyFallbackDisabled(doc))
            return false;

        int fotmIndex = doc.NamedConstructionPlanes.Find(LegacyFotmCPlaneName);
        if (fotmIndex >= 0)
        {
            candidate = new LegacyProjectBaseCandidate(
                LegacyFotmCPlaneName,
                doc.NamedConstructionPlanes[fotmIndex].Plane,
                LegacyProjectBaseConvention.WorldToLocal);
            return true;
        }

        int georefIndex = doc.NamedConstructionPlanes.Find(LegacyProjectBaseCPlaneName);
        if (georefIndex < 0)
            return false;

        candidate = new LegacyProjectBaseCandidate(
            LegacyProjectBaseCPlaneName,
            doc.NamedConstructionPlanes[georefIndex].Plane,
            LegacyProjectBaseConvention.LocalToWorld);
        return true;
    }

    public static bool TryMigrateLegacyProjectBase(
        RhinoDoc doc,
        LegacyProjectBaseCandidate candidate,
        out string message)
    {
        message = string.Empty;
        if (!TryConvertLegacyPlaneToLocalToWorld(
                candidate.Plane,
                candidate.Convention,
                out Plane localToWorld,
                out string? error))
        {
            message = $"Legacy named CPlane '{candidate.Name}' could not be migrated. {error}";
            return false;
        }

        if (!SaveProjectBasePlane(doc, localToWorld))
        {
            message = $"Could not save migrated project base as '{ProjectBaseCPlaneName}'.";
            return false;
        }

        string conventionDescription = candidate.Convention == LegacyProjectBaseConvention.WorldToLocal
            ? "real-world-to-local"
            : "local-to-real-world";
        message =
            $"Migrated legacy {conventionDescription} CPlane '{candidate.Name}' to '{ProjectBaseCPlaneName}'. " +
            "The legacy CPlane was retained unchanged.";
        return true;
    }

    public static bool SaveProjectBasePlane(RhinoDoc doc, Plane plane)
    {
        if (!TryValidateProjectBasePlane(plane, out _))
            return false;

        int previousIndex = doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneName);
        Plane? previousPlane = previousIndex >= 0
            ? doc.NamedConstructionPlanes[previousIndex].Plane
            : null;
        if (previousIndex >= 0 && !doc.NamedConstructionPlanes.Delete(previousIndex))
            return false;

        if (doc.NamedConstructionPlanes.Add(ProjectBaseCPlaneName, plane) >= 0)
        {
            doc.Strings.Delete(DocumentStringSection, LegacyFallbackDisabledEntry);
            return true;
        }

        if (previousPlane.HasValue)
            doc.NamedConstructionPlanes.Add(ProjectBaseCPlaneName, previousPlane.Value);
        return false;
    }

    public static bool ClearProjectBasePlane(RhinoDoc doc)
    {
        int modernIndex = doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneName);
        bool removedModern = modernIndex >= 0 && doc.NamedConstructionPlanes.Delete(modernIndex);
        if (modernIndex >= 0 && !removedModern)
            return false;

        bool hadLegacyFallback = TryGetLegacyCandidate(doc, out _);
        if (hadLegacyFallback)
            SuppressLegacyFallback(doc);

        return removedModern || hadLegacyFallback;
    }

    public static void SuppressLegacyFallback(RhinoDoc doc)
    {
        doc.Strings.SetString(DocumentStringSection, LegacyFallbackDisabledEntry, "true");
    }

    public static bool TryGetTransform(
        bool toProjectCoordinates,
        RhinoDoc doc,
        out Transform transform,
        out string? error)
    {
        transform = Transform.Identity;
        if (!TryGetProjectBasePlane(doc, out Plane plane, out error))
            return false;

        transform = toProjectCoordinates
            ? Transform.PlaneToPlane(plane, Plane.WorldXY)
            : Transform.PlaneToPlane(Plane.WorldXY, plane);
        if (transform.IsValid && transform.TryGetInverse(out _))
            return true;

        error = "The saved project-base transform is invalid or not invertible.";
        return false;
    }

    public static Result OrientDocument(RhinoDoc doc, Point3d basePoint, Point3d? xAxisPoint, out string message)
    {
        message = string.Empty;

        bool hadExisting = HasProjectBasePlane(doc);
        Plane existing = Plane.WorldXY;
        if (hadExisting && !TryGetProjectBasePlane(doc, out existing, out string? existingError))
        {
            message = existingError ?? "The saved project base is invalid.";
            return Result.Failure;
        }

        Point3d zeroBase = new(basePoint.X, basePoint.Y, 0.0);
        var translation = Transform.Translation(-zeroBase.X, -zeroBase.Y, 0.0);
        Transform rotation = Transform.Identity;

        if (xAxisPoint.HasValue)
        {
            var zeroX = new Point3d(xAxisPoint.Value.X, xAxisPoint.Value.Y, 0.0);
            Vector3d direction = zeroX - zeroBase;
            if (!TryUnitizeXAxisDirection(direction, doc.ModelAbsoluteTolerance, out direction))
            {
                message = "The X-axis reference point must be separated from the base point in XY by more than the model tolerance.";
                return Result.Failure;
            }

            rotation = Transform.Rotation(direction, Vector3d.XAxis, Point3d.Origin);
        }

        Transform localTransform = rotation * translation;
        if (!localTransform.TryGetInverse(out Transform inverseLocalTransform))
        {
            message = "The project-base transform is not invertible.";
            return Result.Failure;
        }

        var objectIds = doc.Objects
            .GetObjectList(new ObjectEnumeratorSettings
            {
                ActiveObjects = true,
                DeletedObjects = false,
                LockedObjects = true,
                HiddenObjects = true,
                NormalObjects = true,
                ReferenceObjects = false
            })
            .Where(obj => IsSupportedOrientSpace(obj.Attributes.Space))
            .Select(obj => obj.Id)
            .Where(id => id != Guid.Empty)
            .ToArray();

        Guid[] currentObjectIds = objectIds;
        if (objectIds.Length > 0 &&
            !CommandScriptRunner.RunTransformScript(
                doc,
                objectIds,
                localTransform,
                out string? transformError,
                out currentObjectIds))
        {
            message = transformError ?? "Failed to orient document objects.";
            return Result.Failure;
        }

        Transform existingLocalToWorld = hadExisting
            ? Transform.PlaneToPlane(Plane.WorldXY, existing)
            : Transform.Identity;
        Transform updatedLocalToWorld = ComposeUpdatedLocalToWorld(existingLocalToWorld, inverseLocalTransform);
        Plane projectBase = Plane.WorldXY;
        if (!projectBase.Transform(updatedLocalToWorld) ||
            !TryValidateProjectBasePlane(projectBase, out _))
        {
            RollBackOrientation(doc, currentObjectIds, inverseLocalTransform);
            message = "Could not construct the updated project-base plane; document orientation was rolled back.";
            return Result.Failure;
        }

        if (!SaveProjectBasePlane(doc, projectBase))
        {
            RollBackOrientation(doc, currentObjectIds, inverseLocalTransform);
            message = "Could not save the project-base plane; document orientation was rolled back.";
            return Result.Failure;
        }

        message = hadExisting
            ? $"Updated named CPlane '{ProjectBaseCPlaneName}'."
            : $"Created named CPlane '{ProjectBaseCPlaneName}'.";
        doc.Views.Redraw();
        return Result.Success;
    }

    internal static bool TryConvertLegacyPlaneToLocalToWorld(
        Plane legacyPlane,
        LegacyProjectBaseConvention convention,
        out Plane localToWorldPlane,
        out string? error)
    {
        localToWorldPlane = legacyPlane;
        if (!TryValidateProjectBasePlane(legacyPlane, out error))
            return false;

        if (convention == LegacyProjectBaseConvention.WorldToLocal)
            localToWorldPlane = InvertRigidPlaneTransform(legacyPlane);

        if (!TryValidateProjectBasePlane(localToWorldPlane, out error))
        {
            error ??= "The migrated local-to-real-world plane is invalid.";
            return false;
        }

        return true;
    }

    internal static bool TryValidateProjectBasePlane(Plane plane, out string? error)
    {
        error = null;
        if (!plane.Origin.IsValid || !plane.XAxis.IsValid || !plane.YAxis.IsValid || !plane.ZAxis.IsValid)
        {
            error = "The plane contains invalid geometry.";
            return false;
        }

        double originTolerance = AxisTolerance;
        if (Math.Abs(plane.OriginZ) > originTolerance)
        {
            error = "Its origin must lie on World XY because vertical datum offsets are not supported.";
            return false;
        }

        Vector3d xAxis = plane.XAxis;
        Vector3d yAxis = plane.YAxis;
        Vector3d zAxis = plane.ZAxis;
        bool axesAreUnit = Math.Abs(xAxis.Length - 1.0) <= AxisTolerance &&
                           Math.Abs(yAxis.Length - 1.0) <= AxisTolerance &&
                           Math.Abs(zAxis.Length - 1.0) <= AxisTolerance;
        bool axesAreOrthogonal = Math.Abs(xAxis * yAxis) <= AxisTolerance;
        bool axesAreHorizontal = Math.Abs(xAxis.Z) <= AxisTolerance &&
                                 Math.Abs(yAxis.Z) <= AxisTolerance &&
                                 zAxis * Vector3d.ZAxis >= 1.0 - AxisTolerance;
        double crossDotZ = ((xAxis.Y * yAxis.Z) - (xAxis.Z * yAxis.Y)) * zAxis.X +
                           ((xAxis.Z * yAxis.X) - (xAxis.X * yAxis.Z)) * zAxis.Y +
                           ((xAxis.X * yAxis.Y) - (xAxis.Y * yAxis.X)) * zAxis.Z;
        bool axesAreRightHanded = crossDotZ >= 1.0 - AxisTolerance;
        if (!axesAreUnit || !axesAreOrthogonal || !axesAreHorizontal || !axesAreRightHanded)
        {
            error = "Its axes must form a horizontal, orthonormal, right-handed XY frame.";
            return false;
        }

        return true;
    }

    internal static bool TryUnitizeXAxisDirection(
        Vector3d direction,
        double modelTolerance,
        out Vector3d unitDirection)
    {
        unitDirection = direction;
        double tolerance = double.IsFinite(modelTolerance) && modelTolerance > 0.0
            ? modelTolerance
            : AxisTolerance;
        if (!direction.IsValid || direction.SquareLength <= tolerance * tolerance)
            return false;

        double length = Math.Sqrt(direction.SquareLength);
        if (!double.IsFinite(length) || length <= 0.0)
            return false;

        unitDirection = direction / length;
        return unitDirection.IsValid;
    }

    internal static bool IsSupportedOrientSpace(ActiveSpace space)
    {
        return space == ActiveSpace.ModelSpace;
    }

    internal static Transform ComposeUpdatedLocalToWorld(
        Transform existingLocalToWorld,
        Transform inverseNewLocalTransform)
    {
        return existingLocalToWorld * inverseNewLocalTransform;
    }

    private static bool IsLegacyFallbackDisabled(RhinoDoc doc)
    {
        return bool.TryParse(
                   doc.Strings.GetValue(DocumentStringSection, LegacyFallbackDisabledEntry),
                   out bool disabled) &&
               disabled;
    }

    private static Plane InvertRigidPlaneTransform(Plane plane)
    {
        Vector3d xAxis = plane.XAxis;
        Vector3d yAxis = plane.YAxis;
        Vector3d zAxis = plane.ZAxis;
        Point3d origin = plane.Origin;

        Plane inverse = Plane.WorldXY;
        inverse.XAxis = new Vector3d(xAxis.X, yAxis.X, zAxis.X);
        inverse.YAxis = new Vector3d(xAxis.Y, yAxis.Y, zAxis.Y);
        inverse.ZAxis = new Vector3d(xAxis.Z, yAxis.Z, zAxis.Z);
        inverse.Origin = new Point3d(
            -((xAxis.X * origin.X) + (xAxis.Y * origin.Y) + (xAxis.Z * origin.Z)),
            -((yAxis.X * origin.X) + (yAxis.Y * origin.Y) + (yAxis.Z * origin.Z)),
            -((zAxis.X * origin.X) + (zAxis.Y * origin.Y) + (zAxis.Z * origin.Z)));
        return inverse;
    }

    private static void RollBackOrientation(
        RhinoDoc doc,
        IReadOnlyList<Guid> currentObjectIds,
        Transform inverseLocalTransform)
    {
        if (currentObjectIds.Count > 0)
            CommandScriptRunner.RunTransformScript(doc, currentObjectIds, inverseLocalTransform, out _, out _);
    }
}
