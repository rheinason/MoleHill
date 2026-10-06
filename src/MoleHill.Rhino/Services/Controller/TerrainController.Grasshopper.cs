// TerrainController projection of final display state and document zones into reflection DTOs.
using MoleHill.Core.Engine;
using MoleHill.Interop;
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
        TerrainDefinition? terrain = ResolveGrasshopperTerrain(doc, terrainKey, out string? resolutionError);
        if (terrain == null)
        {
            errorMessage = resolutionError ?? (string.IsNullOrWhiteSpace(terrainKey)
                ? "The Rhino document has no MoleHill terrain."
                : $"No MoleHill terrain matches '{terrainKey}'.");
            return null;
        }

        string sourceStatus = GetGrasshopperSnapshotStatus(doc, terrainKey, out string? statusMessage);
        if (sourceStatus != "Current")
        {
            errorMessage = statusMessage ?? $"Terrain '{terrain.Name}' is {sourceStatus.ToLowerInvariant()}; its last completed snapshot is stale.";
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
        Curve[] hardCurves = displayState.HardConstraints.Select(CreateConstraintCurve)
            .Where(curve => curve != null).Cast<Curve>().ToArray();
        Curve[] elevationCurves = displayState.ElevationConstraints.Select(CreateConstraintCurve)
            .Where(curve => curve != null).Cast<Curve>().ToArray();
        var resolvedRegions = displayState.TerrainRegions.ToDictionary(region => region.RegionId);
        var snapshot = new TerrainInteropSnapshot
        {
            Mesh = displayState.TerrainMesh!.DuplicateMesh(),
            Name = terrain.Name,
            Key = terrain.TerrainId.ToString("D"),
            Revision = revision,
            Breaklines = hardCurves.Concat(elevationCurves).ToArray(),
            Regions = terrain.Zones.Select((zone, index) =>
            {
                resolvedRegions.TryGetValue(zone.ZoneId, out TerrainRegionState? region);
                return new TerrainInteropRegion
                {
                    Name = zone.Name,
                    Key = zone.ZoneId.ToString("D"),
                    Boundaries = region?.Boundaries.Select(boundary => boundary.DuplicateCurve()).ToArray()
                        ?? Array.Empty<Curve>(),
                    StackIndex = index,
                    IsEnabled = zone.IsEnabled,
                    UseInputElevationForPriority = zone.UseInputElevationForPriority,
                    ColorArgb = zone.ColorArgb,
                    UseColorOverride = zone.UseColorOverride,
                    LayerName = zone.LayerName,
                    MaterialName = zone.MaterialName,
                    SplitToSeparateMesh = zone.SplitToSeparateMesh
                };
            }).ToArray(),
            Diagnostics = diagnostics,
            UnitSystem = unitContext.UnitSystem.ToString(),
            MetersPerModelUnit = unitContext.MetersPerModelUnit,
            LocalToWorld = hasProjectBaseTransform ? localToWorld : Transform.Identity,
            HasProjectBaseTransform = hasProjectBaseTransform
        };
        try
        {
            snapshot.Fingerprint = TerrainSnapshotFingerprint.Compute(
                doc, snapshot, hardCurves, elevationCurves, terrain.Zones);
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    internal string GetGrasshopperSnapshotStatus(RhinoDoc doc, string? terrainKey, out string? message)
    {
        TerrainDefinition? terrain = ResolveGrasshopperTerrain(doc, terrainKey, out message);
        if (terrain == null)
        {
            message ??= string.IsNullOrWhiteSpace(terrainKey)
                ? "No terrain is selected in the MoleHill panel."
                : $"No MoleHill terrain matches '{terrainKey}'.";
            return "Unavailable";
        }

        message = terrain.LastBuildMessage;
        bool hasQueuedOrRunningFinalBuild =
            _pendingRebuilds.ContainsKey((doc.RuntimeSerialNumber, terrain.TerrainId, TerrainBuildMode.Final)) ||
            (_rebuildStates.TryGetValue((doc.RuntimeSerialNumber, terrain.TerrainId), out TerrainRebuildState? state) &&
             state.IsBuilding);
        if (hasQueuedOrRunningFinalBuild)
            return "Rebuilding";
        string firstLine = message?.Split(new[] { Environment.NewLine }, StringSplitOptions.None)[0] ?? string.Empty;
        if (((firstLine.StartsWith("Build #", StringComparison.OrdinalIgnoreCase) ||
              firstLine.StartsWith("Preview #", StringComparison.OrdinalIgnoreCase)) &&
             firstLine.Contains(" failed.", StringComparison.OrdinalIgnoreCase)) ||
            firstLine.Contains("manual rebuild required", StringComparison.OrdinalIgnoreCase))
            return "Failed";
        if (HasPendingFinalBuild(doc.RuntimeSerialNumber, terrain.TerrainId))
            return "Rebuilding";
        TerrainDisplayState? displayState = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).DisplayState;
        return TerrainSnapshotEligibility.GetRejectionReason(
            terrain.Name,
            displayState?.TerrainMesh != null,
            displayState?.IsPreview == true,
            displayState?.HasDeferredOutputs == true) == null
            ? "Current" : "Unavailable";
    }

    internal string? ResolveGrasshopperReferenceKey(RhinoDoc doc, string? terrainKey) =>
        ResolveGrasshopperTerrain(doc, terrainKey, out _)?.TerrainId.ToString("D");

    private TerrainDefinition? ResolveGrasshopperTerrain(RhinoDoc doc, string? terrainKey, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(terrainKey))
            return GetSelectedTerrain(doc);

        return TerrainReferenceResolver.Resolve(GetTerrains(doc), terrainKey, out errorMessage);
    }

    private static Curve? CreateConstraintCurve(ConstraintPolyline constraint)
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
