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

    public static void SaveProjectBasePlane(RhinoDoc doc, Plane plane)
    {
        string name = GetProjectBasePlaneName(doc);
        foreach (string candidate in EnumerateCandidateNames())
        {
            if (doc.NamedConstructionPlanes.Find(candidate) >= 0)
                doc.NamedConstructionPlanes.Delete(candidate);
        }

        doc.NamedConstructionPlanes.Add(name, plane);
    }

    public static bool TryGetTransform(bool removeGeoref, RhinoDoc doc, out Transform transform)
    {
        transform = Transform.Identity;
        if (!TryGetProjectBasePlane(doc, out Plane plane))
            return false;

        transform = removeGeoref
            ? Transform.PlaneToPlane(plane, Plane.WorldXY)
            : Transform.PlaneToPlane(Plane.WorldXY, plane);
        return transform.IsValid;
    }

    public static Result OrientDocument(RhinoDoc doc, Point3d basePoint, Point3d? xAxisPoint, out string message)
    {
        message = string.Empty;

        Point3d zeroBase = new(basePoint.X, basePoint.Y, 0.0);
        var translation = Transform.Translation(-zeroBase.X, -zeroBase.Y, 0.0);
        var inverseTranslation = Transform.Translation(zeroBase.X, zeroBase.Y, 0.0);
        Transform rotation = Transform.Identity;
        Transform inverseRotation = Transform.Identity;

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
            inverseRotation = Transform.Rotation(Vector3d.XAxis, direction, Point3d.Origin);
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
            .Select(obj => obj.Id)
            .Where(id => id != Guid.Empty)
            .ToArray();

        if (objectIds.Length > 0)
        {
            Guid[] transformedIds = objectIds;
            if (!CommandScriptRunner.RunTransformScript(doc, transformedIds, translation, out string? translationError, out transformedIds))
            {
                message = translationError ?? "Failed to translate document objects.";
                return Result.Failure;
            }

            if (!rotation.IsIdentity && !CommandScriptRunner.RunTransformScript(doc, transformedIds, rotation, out string? rotationError, out transformedIds))
            {
                message = rotationError ?? "Failed to rotate document objects.";
                return Result.Failure;
            }
        }

        bool hadExisting = TryGetProjectBasePlane(doc, out Plane existing);
        Plane projectBase = hadExisting
            ? existing
            : Plane.WorldXY;
        if (!inverseRotation.IsIdentity)
            projectBase.Transform(inverseRotation);
        projectBase.Transform(inverseTranslation);

        SaveProjectBasePlane(doc, projectBase);
        message = hadExisting
            ? $"Updated named CPlane '{GetProjectBasePlaneName(doc)}'."
            : $"Created named CPlane '{GetProjectBasePlaneName(doc)}'.";
        doc.Views.Redraw();
        return Result.Success;
    }

    private static IEnumerable<string> EnumerateCandidateNames()
    {
        yield return LegacyProjectBaseCPlaneName;
        yield return ProjectBaseCPlaneName;
    }
}
