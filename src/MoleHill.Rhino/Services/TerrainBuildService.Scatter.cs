using System.Diagnostics;
using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Scatter stage: generate randomized block instances across the terrain inside boundary regions.
// Reuses the object-placement helpers (terrain sampling/frame, deterministic random) from the Objects
// partial; emits GeneratedRhinoObject block instances the conduit previews and bake materialises.
internal sealed partial class TerrainBuildService
{
    private static void BuildScatterPlacements(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        var scatters = terrain.Objects
            .OfType<ScatterObjectDefinition>()
            .Where(item => item.IsEnabled)
            .ToList();
        if (scatters.Count == 0)
            return;

        var timer = Stopwatch.StartNew();
        mesh.Normals.ComputeNormals();
        int emitted = 0;

        foreach (var definition in scatters)
        {
            ThrowIfCancellationRequested(shouldCancel);

            var loops = new List<double[]>();
            foreach (Curve curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, definition.Boundaries))
            {
                if (TryCurveToXyLoop(curve, snapshot.ModelAbsoluteTolerance, out double[] loop))
                    loops.Add(loop);
            }

            if (loops.Count == 0)
            {
                build.Diagnostics.Add($"Scatter '{definition.Name}' skipped: no closed boundary curves were resolved.");
                continue;
            }

            var blocks = ResolveScatterBlocks(snapshot, definition);
            if (blocks.Count == 0)
            {
                build.Diagnostics.Add($"Scatter '{definition.Name}' skipped: no block instances were selected.");
                continue;
            }

            double totalWeight = blocks.Sum(entry => entry.Weight);
            if (totalWeight <= 0.0)
            {
                build.Diagnostics.Add($"Scatter '{definition.Name}' skipped: block weights sum to zero.");
                continue;
            }

            var points = ScatterSampler.Sample(new ScatterRequest
            {
                Boundaries = loops,
                Pattern = definition.Pattern,
                DensityMode = definition.DensityMode,
                Count = definition.Count,
                PerAreaDensity = definition.PerAreaDensity,
                Spacing = definition.Spacing,
                Seed = definition.RandomSeed
            });

            int placed = 0;
            for (int index = 0; index < points.Count; index++)
            {
                if ((index & 63) == 0)
                    ThrowIfCancellationRequested(shouldCancel);

                (double px, double py) = points[index];
                if (!TryResolveTerrainPoint(snapshot, mesh, new Point3d(px, py, 0.0), out Point3d terrainPoint, out Vector3d terrainNormal, out _))
                    continue;

                if (definition.SlopeFilterEnabled)
                {
                    double slopeDegrees = SlopeDegreesFromNormal(terrainNormal);
                    if (slopeDegrees < definition.SlopeMinDegrees - 1e-9 || slopeDegrees > definition.SlopeMaxDegrees + 1e-9)
                        continue;
                }

                if (definition.ElevationFilterEnabled &&
                    (terrainPoint.Z < definition.ElevationMin - 1e-9 || terrainPoint.Z > definition.ElevationMax + 1e-9))
                {
                    continue;
                }

                Vector3d up = definition.AlignToSlope ? terrainNormal : Vector3d.ZAxis;
                Plane frame;
                if (definition.AlignToSlope)
                {
                    if (!TryCreateTerrainFrame(terrainPoint, terrainNormal, Vector3d.XAxis, out frame))
                        frame = new Plane(terrainPoint, Vector3d.XAxis, Vector3d.YAxis);
                }
                else
                {
                    frame = new Plane(terrainPoint, Vector3d.XAxis, Vector3d.YAxis);
                }

                if (Math.Abs(definition.ZOffset) > 1e-9)
                    frame.Origin += frame.Normal * definition.ZOffset;

                Guid instanceKey = InstanceKeyFromIndex(index);
                string blockName = PickWeightedBlock(blocks, totalWeight, definition.Id, instanceKey, definition.RandomSeed);
                Transform basePlacement = Transform.PlaneToPlane(Plane.WorldXY, frame);
                Transform random = CreateRandomPlacementTransform(definition, instanceKey, frame.Origin, up);
                Transform instanceTransform = random * basePlacement;

                build.ScatterObjects.Add(new GeneratedRhinoObject
                {
                    Name = definition.Name,
                    InstanceDefinitionName = blockName,
                    InstanceTransform = instanceTransform,
                    ScatterDefinitionId = definition.Id
                });
                placed++;
            }

            emitted += placed;
            if (placed == 0)
                build.Diagnostics.Add($"Scatter '{definition.Name}' produced no instances (all samples fell outside the terrain or its filters).");
        }

        timer.Stop();
        build.RecordTiming(
            "Scatter",
            timer.Elapsed,
            $"{scatters.Count:N0} scatter definitions produced {emitted:N0} instances",
            StageTimingDiagnosticThresholdMs);
    }

    private static List<(string Name, double Weight)> ResolveScatterBlocks(
        TerrainBuildSnapshot snapshot,
        ScatterObjectDefinition definition)
    {
        var weightByName = new Dictionary<string, double>(StringComparer.Ordinal);

        void Accumulate(SourceReferenceSet source, double weight)
        {
            if (weight <= 0.0)
                return;

            foreach (var resolved in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, source))
            {
                if (string.IsNullOrWhiteSpace(resolved.InstanceDefinitionName))
                    continue;

                weightByName.TryGetValue(resolved.InstanceDefinitionName!, out double current);
                weightByName[resolved.InstanceDefinitionName!] = current + weight;
            }
        }

        if (definition.Blocks.Count > 0)
        {
            foreach (var entry in definition.Blocks)
                Accumulate(entry.Source, Math.Max(entry.Weight, 0.0));
        }
        else
        {
            Accumulate(definition.Sources, 1.0);
        }

        return weightByName
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
    }

    private static string PickWeightedBlock(
        List<(string Name, double Weight)> blocks,
        double totalWeight,
        Guid definitionId,
        Guid instanceKey,
        int seed)
    {
        if (blocks.Count == 1)
            return blocks[0].Name;

        double pick = SampleDeterministicUnit(definitionId, instanceKey, seed, 2) * totalWeight;
        double accumulated = 0.0;
        foreach (var (name, weight) in blocks)
        {
            accumulated += weight;
            if (pick <= accumulated)
                return name;
        }

        return blocks[^1].Name;
    }

    private static double SlopeDegreesFromNormal(Vector3d normal)
    {
        double horizontal = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y);
        double slopeRadians = Math.Atan2(horizontal, Math.Abs(normal.Z));
        return slopeRadians * 180.0 / Math.PI;
    }

    private static Guid InstanceKeyFromIndex(int index)
    {
        // Stable per-instance key so the deterministic random transforms reproduce between rebuilds.
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), index);
        return new Guid(bytes);
    }

    private static bool TryCurveToXyLoop(Curve curve, double tolerance, out double[] loop)
    {
        loop = Array.Empty<double>();
        if (curve == null)
            return false;

        Polyline polyline;
        if (!curve.TryGetPolyline(out polyline) || !polyline.IsValid || polyline.Count < 4)
        {
            double chord = Math.Max(tolerance * 10.0, 1e-3);
            int segments = Math.Clamp((int)Math.Ceiling(curve.GetLength() / chord), 24, 512);
            double[]? parameters = curve.DivideByCount(segments, includeEnds: true);
            if (parameters == null || parameters.Length < 4)
                return false;

            polyline = new Polyline(parameters.Select(curve.PointAt));
        }

        int count = polyline.Count;
        // Drop a duplicated closing vertex so the loop is non-repeating.
        if (count >= 2 && polyline[0].DistanceTo(polyline[count - 1]) <= Math.Max(tolerance, 1e-9))
            count--;

        if (count < 3)
            return false;

        loop = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            loop[i * 2] = polyline[i].X;
            loop[i * 2 + 1] = polyline[i].Y;
        }

        return true;
    }
}
