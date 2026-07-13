using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class ProjectBaseCPlaneService
{
    public const string ProjectBaseCPlaneName = "MoleHill_ProjectBase";
    public const string LegacyProjectBaseCPlaneName = "Georef";

    public static bool HasProjectBasePlane(RhinoDoc doc)
    {
        return TryGetProjectBasePlane(doc, out _);
    }

    public static string GetProjectBasePlaneName(RhinoDoc doc)
    {
        foreach (string name in EnumerateCandidateNames())
        {
            if (doc.NamedConstructionPlanes.Find(name) >= 0)
                return name;
        }

        return ProjectBaseCPlaneName;
    }

    public static bool TryGetProjectBasePlane(RhinoDoc doc, out Plane plane)
    {
        plane = Plane.WorldXY;
        int index = -1;
        foreach (string name in EnumerateCandidateNames())
        {
            index = doc.NamedConstructionPlanes.Find(name);
            if (index >= 0)
                break;
        }

        if (index < 0)
            return false;

        var constructionPlane = doc.NamedConstructionPlanes[index];
        plane = constructionPlane.Plane;
        return true;
    }

    public static bool SaveProjectBasePlane(RhinoDoc doc, Plane plane)
    {
        var previousPlanes = new List<(string Name, Plane Plane)>();
        foreach (string candidate in EnumerateCandidateNames())
        {
            int index = doc.NamedConstructionPlanes.Find(candidate);
            if (index < 0)
                continue;

            previousPlanes.Add((candidate, doc.NamedConstructionPlanes[index].Plane));
            doc.NamedConstructionPlanes.Delete(index);
        }

        if (doc.NamedConstructionPlanes.Add(ProjectBaseCPlaneName, plane) >= 0)
            return true;

        foreach ((string name, Plane previousPlane) in previousPlanes)
            doc.NamedConstructionPlanes.Add(name, previousPlane);
        return false;
    }

    public static bool ClearProjectBasePlane(RhinoDoc doc)
    {
        bool removed = false;
        foreach (string candidate in EnumerateCandidateNames())
        {
            int index = doc.NamedConstructionPlanes.Find(candidate);
            if (index < 0)
                continue;

            removed |= doc.NamedConstructionPlanes.Delete(index);
        }

        return removed;
    }

    public static bool TryGetTransform(bool toProjectCoordinates, RhinoDoc doc, out Transform transform)
    {
        transform = Transform.Identity;
        if (!TryGetProjectBasePlane(doc, out Plane plane))
            return false;

        transform = toProjectCoordinates
            ? Transform.PlaneToPlane(plane, Plane.WorldXY)
            : Transform.PlaneToPlane(Plane.WorldXY, plane);
        return transform.IsValid;
    }

    public static Result OrientDocument(RhinoDoc doc, Point3d basePoint, Point3d? xAxisPoint, out string message)
    {
        message = string.Empty;

        Point3d zeroBase = new(basePoint.X, basePoint.Y, 0.0);
        var translation = Transform.Translation(-zeroBase.X, -zeroBase.Y, 0.0);
        Transform rotation = Transform.Identity;

        if (xAxisPoint.HasValue)
        {
            var zeroX = new Point3d(xAxisPoint.Value.X, xAxisPoint.Value.Y, 0.0);
            Vector3d direction = zeroX - zeroBase;
            if (!direction.Unitize())
            {
                message = "The X-axis reference point must be different from the base point.";
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
        if (objectIds.Length > 0)
        {
            if (!CommandScriptRunner.RunTransformScript(doc, objectIds, localTransform, out string? transformError, out currentObjectIds))
            {
                message = transformError ?? "Failed to orient document objects.";
                return Result.Failure;
            }
        }

        bool hadExisting = TryGetProjectBasePlane(doc, out Plane existing);
        Transform existingLocalToWorld = hadExisting
            ? Transform.PlaneToPlane(Plane.WorldXY, existing)
            : Transform.Identity;
        Transform updatedLocalToWorld = ComposeUpdatedLocalToWorld(existingLocalToWorld, inverseLocalTransform);
        Plane projectBase = Plane.WorldXY;
        if (!projectBase.Transform(updatedLocalToWorld) || !projectBase.IsValid)
        {
            // Geometry has already moved, but this should only be reachable for a Rhino transform
            // construction failure. Move it back before returning so document and georef stay paired.
            if (currentObjectIds.Length > 0)
                CommandScriptRunner.RunTransformScript(doc, currentObjectIds, inverseLocalTransform, out _, out _);
            message = "Could not construct the updated project-base plane; document orientation was rolled back.";
            return Result.Failure;
        }

        if (!SaveProjectBasePlane(doc, projectBase))
        {
            if (currentObjectIds.Length > 0)
                CommandScriptRunner.RunTransformScript(doc, currentObjectIds, inverseLocalTransform, out _, out _);
            message = "Could not save the project-base plane; document orientation was rolled back.";
            return Result.Failure;
        }
        message = hadExisting
            ? $"Updated named CPlane '{GetProjectBasePlaneName(doc)}'."
            : $"Created named CPlane '{GetProjectBasePlaneName(doc)}'.";
        doc.Views.Redraw();
        return Result.Success;
    }

    internal static bool IsSupportedOrientSpace(ActiveSpace space)
    {
        return space == ActiveSpace.ModelSpace;
    }

    private static IEnumerable<string> EnumerateCandidateNames()
    {
        yield return ProjectBaseCPlaneName;
        yield return LegacyProjectBaseCPlaneName;
    }

    internal static Transform ComposeUpdatedLocalToWorld(
        Transform existingLocalToWorld,
        Transform inverseNewLocalTransform)
    {
        return existingLocalToWorld * inverseNewLocalTransform;
    }
}
