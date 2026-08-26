// TerrainController projection of final display state and document zones into reflection DTOs.
using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainController
{
    internal TerrainInteropSnapshot? TryCreateGrasshopperSnapshot(
        RhinoDoc doc,
        string? terrainKey,
        out string? errorMessage)
    {
        errorMessage = null;
        TerrainDefinition? terrain = ResolveGrasshopperTerrain(doc, terrainKey);
        if (terrain == null)
        {
            errorMessage = string.IsNullOrWhiteSpace(terrainKey)
                ? "The Rhino document has no MoleHill terrain."
                : $"No MoleHill terrain matches '{terrainKey}'.";
            return null;
        }

        TerrainRuntimeCache runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
        TerrainDisplayState? displayState = runtimeCache.DisplayState;
        string? rejectionReason = TerrainSnapshotEligibility.GetRejectionReason(
            terrain.Name,
            displayState?.TerrainMesh != null,
            displayState?.IsPreview == true,
            displayState?.HasDeferredOutputs == true);
        if (rejectionReason != null)
        {
            errorMessage = rejectionReason;
            return null;
        }

        // Eligibility above guarantees a completed final display state.
        displayState = runtimeCache.DisplayState!;

        var diagnostics = new List<string>();
        if (!string.IsNullOrWhiteSpace(terrain.LastBuildMessage))
        {
            diagnostics.AddRange(terrain.LastBuildMessage
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        long revision = _rebuildStates.TryGetValue((doc.RuntimeSerialNumber, terrain.TerrainId), out TerrainRebuildState? state)
            ? state.AppliedVersion
            : 0;
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(doc);
        bool hasProjectBaseTransform = ProjectBaseCPlaneService.TryGetTransform(
            toProjectCoordinates: false,
            doc,
            out Transform localToWorld,
            out _);
        return new TerrainInteropSnapshot
        {
            Mesh = displayState.TerrainMesh!.DuplicateMesh(),
            Name = terrain.Name,
            Key = terrain.TerrainId.ToString("D"),
            Revision = revision,
            Breaklines = displayState.HardConstraints
                .Concat(displayState.ElevationConstraints)
                .Select(CreateConstraintCurve)
                .Where(curve => curve != null)
                .Cast<Curve>()
                .ToArray(),
            Regions = displayState.TerrainRegions.Select(region => new TerrainInteropRegion
            {
                Name = region.Name,
                Key = region.RegionId.ToString("D"),
                Boundaries = region.Boundaries.Select(boundary => boundary.DuplicateCurve()).ToArray()
            }).ToArray(),
            Diagnostics = diagnostics,
            UnitSystem = unitContext.UnitSystem.ToString(),
            MetersPerModelUnit = unitContext.MetersPerModelUnit,
            LocalToWorld = hasProjectBaseTransform ? localToWorld : Transform.Identity,
            HasProjectBaseTransform = hasProjectBaseTransform
        };
    }

    private TerrainDefinition? ResolveGrasshopperTerrain(RhinoDoc doc, string? terrainKey)
    {
        if (string.IsNullOrWhiteSpace(terrainKey))
            return GetSelectedTerrain(doc);

        IReadOnlyList<TerrainDefinition> terrains = GetTerrains(doc);
        if (Guid.TryParse(terrainKey, out Guid terrainId))
            return terrains.FirstOrDefault(terrain => terrain.TerrainId == terrainId);

        return terrains.FirstOrDefault(terrain =>
            string.Equals(terrain.Name, terrainKey, StringComparison.OrdinalIgnoreCase));
    }

    private static Curve? CreateConstraintCurve(SurfaceRemesher.ConstraintPolyline constraint)
    {
        if (constraint.PointCount < 2 || constraint.Points.Length < constraint.PointCount * 3)
            return null;

        int outputCount = constraint.PointCount + (constraint.IsClosed ? 1 : 0);
        var points = new Point3d[outputCount];
        for (int index = 0; index < constraint.PointCount; index++)
        {
            points[index] = new Point3d(
                constraint.Points[index * 3],
                constraint.Points[index * 3 + 1],
                constraint.Points[index * 3 + 2]);
        }

        if (constraint.IsClosed)
            points[^1] = points[0];
        return new PolylineCurve(points);
    }

}
