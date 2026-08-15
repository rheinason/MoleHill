using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

internal readonly record struct GeoreferenceObjectState(Guid Id, ActiveSpace Space);

/// <summary>Pure before/after object-table differencing for transactional georeferenced imports.</summary>
internal static class GeoreferenceImportPlanner
{
    public static Guid[] FindNewObjectIds(
        IEnumerable<GeoreferenceObjectState> before,
        IEnumerable<GeoreferenceObjectState> after)
    {
        var beforeIds = before
            .Where(item => item.Id != Guid.Empty)
            .Select(item => item.Id)
            .ToHashSet();

        return after
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty && !beforeIds.Contains(id))
            .Distinct()
            .ToArray();
    }

    public static Guid[] FindNewModelObjectIds(
        IEnumerable<GeoreferenceObjectState> before,
        IEnumerable<GeoreferenceObjectState> after)
    {
        var beforeIds = before
            .Where(item => item.Id != Guid.Empty)
            .Select(item => item.Id)
            .ToHashSet();

        return after
            .Where(item => item.Space == ActiveSpace.ModelSpace)
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty && !beforeIds.Contains(id))
            .Distinct()
            .ToArray();
    }
}
