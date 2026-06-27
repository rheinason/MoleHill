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

            var blocks = ResolveScatterBlocks(snapshot, definition, out var extentByName);
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

            // Block selection per slot. Curve + Sequence cycles the mix in order (repeating pattern);
            // everything else picks weighted-random. The same index must select the same block at
            // footprint time (edge-to-edge) and placement time, so extents and instances stay in sync.
            string SelectBlockName(int slotIndex, Guid instanceKey)
            {
                if (definition.SourceMode == ScatterSourceMode.Curve &&
                    definition.BlockOrder == ScatterBlockOrder.Sequence &&
                    blocks.Count > 1)
                {
                    int wrapped = ((slotIndex % blocks.Count) + blocks.Count) % blocks.Count;
                    return blocks[wrapped].Name;
                }

                return PickWeightedBlock(blocks, totalWeight, definition.Id, instanceKey, definition.RandomSeed);
            }

            // Sample the scatter domain into a flat list of (point, optional curve tangent).
            var samples = new List<(double X, double Y, double? Tangent)>();
            if (definition.SourceMode == ScatterSourceMode.Curve)
            {
                var paths = new List<double[]>();
                foreach (Curve curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, definition.Paths))
                {
                    if (TryCurveToXyPolyline(curve, snapshot.ModelAbsoluteTolerance, out double[] polyline))
                        paths.Add(polyline);
                }

                if (paths.Count == 0)
                {
                    build.Diagnostics.Add($"Scatter '{definition.Name}' skipped: no curves were resolved.");
                    continue;
                }

                // Edge-to-edge needs each item's along-curve footprint. ResolveScatterBlocks already folds
                // in worst-case random scale + rotation/orientation so blocks never overlap; just look the
                // extent up for the block this slot will use (same selector as placement → in sync).
                Func<int, double>? itemExtent = definition.DensityMode == ScatterDensityMode.EdgeToEdge
                    ? index =>
                    {
                        string name = SelectBlockName(index, InstanceKeyFromIndex(index));
                        return extentByName.TryGetValue(name, out double e) && e > 0.0 ? e : 1.0;
                    }
                    : null;

                var request = new ScatterRequest
                {
                    Source = ScatterSourceMode.Curve,
                    Paths = paths,
                    DensityMode = definition.DensityMode,
                    Count = definition.Count,
                    Spacing = definition.Spacing,
                    EdgeGap = definition.EdgeGap,
                    JitterXy = definition.JitterXy,
                    AlongJitter = definition.AlongJitter,
                    Seed = definition.RandomSeed,
                    ShouldCancel = shouldCancel
                };

                foreach (ScatterCurvePoint point in ScatterSampler.SampleCurve(request, itemExtent))
                    samples.Add((point.X, point.Y, point.TangentRadians));
            }
            else
            {
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

                var request = new ScatterRequest
                {
                    Boundaries = loops,
                    Pattern = definition.Pattern,
                    DensityMode = definition.DensityMode,
                    Count = definition.Count,
                    PerAreaDensity = definition.PerAreaDensity,
                    Spacing = definition.Spacing,
                    Seed = definition.RandomSeed,
                    // Make the sampler itself abortable so a superseded build doesn't hang inside a
                    // runaway sample; ThrowIfCancellationRequested below unwinds the rest.
                    ShouldCancel = shouldCancel
                };

                foreach ((double px, double py) in ScatterSampler.Sample(request))
                    samples.Add((px, py, null));
            }

            int placed = 0;
            for (int index = 0; index < samples.Count; index++)
            {
                if ((index & 63) == 0)
                    ThrowIfCancellationRequested(shouldCancel);

                (double px, double py, double? tangent) = samples[index];
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

                // Align-to-tangent (curve mode) sets the frame's X axis along the curve direction.
                Vector3d referenceX = definition.SourceMode == ScatterSourceMode.Curve &&
                                      definition.AlignToTangent && tangent.HasValue
                    ? new Vector3d(Math.Cos(tangent.Value), Math.Sin(tangent.Value), 0.0)
                    : Vector3d.XAxis;

                Vector3d up = definition.AlignToSlope ? terrainNormal : Vector3d.ZAxis;
                Plane frame;
                if (definition.AlignToSlope)
                {
                    if (!TryCreateTerrainFrame(terrainPoint, terrainNormal, referenceX, out frame))
                        frame = new Plane(terrainPoint, referenceX, Vector3d.CrossProduct(Vector3d.ZAxis, referenceX));
                }
                else
                {
                    frame = new Plane(terrainPoint, referenceX, Vector3d.CrossProduct(Vector3d.ZAxis, referenceX));
                }

                if (Math.Abs(definition.ZOffset) > 1e-9)
                    frame.Origin += frame.Normal * definition.ZOffset;

                Guid instanceKey = InstanceKeyFromIndex(index);
                string blockName = SelectBlockName(index, instanceKey);
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
        ScatterObjectDefinition definition,
        out Dictionary<string, double> extentByName)
    {
        var weightByName = new Dictionary<string, double>(StringComparer.Ordinal);
        var boundsByName = new Dictionary<string, BoundingBox>(StringComparer.Ordinal);
        var order = new List<string>(); // first-appearance order — drives Sequence block ordering.

        void Note(string name, double weight, BoundingBox bbox)
        {
            if (string.IsNullOrWhiteSpace(name) || weight <= 0.0)
                return;

            if (!weightByName.ContainsKey(name))
                order.Add(name);

            weightByName.TryGetValue(name, out double current);
            weightByName[name] = current + weight;

            if (bbox.IsValid)
            {
                if (boundsByName.TryGetValue(name, out BoundingBox existing) && existing.IsValid)
                {
                    existing.Union(bbox);
                    boundsByName[name] = existing;
                }
                else
                {
                    boundsByName[name] = bbox;
                }
            }
        }

        void AccumulateSource(SourceReferenceSet source, double weight)
        {
            if (weight <= 0.0)
                return;

            foreach (var resolved in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, source))
            {
                if (string.IsNullOrWhiteSpace(resolved.InstanceDefinitionName))
                    continue;

                Note(resolved.InstanceDefinitionName!, weight, resolved.LocalBoundingBox);
            }
        }

        if (definition.Blocks.Count > 0)
        {
            foreach (var entry in definition.Blocks)
            {
                double weight = Math.Max(entry.Weight, 0.0);
                if (weight <= 0.0)
                    continue;

                if (!string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
                {
                    // Named blocks aren't resolved as source objects, so their size comes from the
                    // snapshot's block-bounds catalog (captured on the main thread).
                    snapshot.BlockDefinitionBounds.TryGetValue(entry.BlockDefinitionName!, out BoundingBox bbox);
                    Note(entry.BlockDefinitionName!, weight, bbox);
                }
                else
                {
                    AccumulateSource(entry.Source, weight);
                }
            }
        }
        else
        {
            AccumulateSource(definition.Sources, 1.0);
        }

        extentByName = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string name in order)
        {
            boundsByName.TryGetValue(name, out BoundingBox bbox);
            extentByName[name] = WorstAlongExtent(bbox, definition);
        }

        return order.Select(name => (name, weightByName[name])).ToList();
    }

    // Worst-case along-curve footprint of a block, so edge-to-edge spacing never overlaps regardless of
    // the per-instance random scale/rotation. Folds in the maximum random scale; for align-to-tangent the
    // worst projection of the axis-aligned footprint over the yaw range, otherwise the diagonal (the
    // tangent can meet the un-aligned block at any angle).
    private static double WorstAlongExtent(BoundingBox bbox, ScatterObjectDefinition definition)
    {
        if (!bbox.IsValid)
            return 0.0;

        double dx = bbox.Max.X - bbox.Min.X;
        double dy = bbox.Max.Y - bbox.Min.Y;
        if (!(dx > 0.0) && !(dy > 0.0))
            return 0.0;

        double scaleMax = Math.Max(0.01, Math.Max(definition.RandomScaleMin, definition.RandomScaleMax));

        double extent;
        if (definition.AlignToTangent)
        {
            double a = Math.Min(definition.RandomRotationMinDegrees, definition.RandomRotationMaxDegrees);
            double b = Math.Max(definition.RandomRotationMinDegrees, definition.RandomRotationMaxDegrees);
            double best = 0.0;
            const int steps = 24;
            for (int i = 0; i <= steps; i++)
            {
                double theta = (a + (b - a) * i / steps) * Math.PI / 180.0;
                double e = (dx * Math.Abs(Math.Cos(theta))) + (dy * Math.Abs(Math.Sin(theta)));
                if (e > best)
                    best = e;
            }

            extent = best;
        }
        else
        {
            extent = Math.Sqrt((dx * dx) + (dy * dy));
        }

        return extent * scaleMax;
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

    private static bool TryCurveToXyPolyline(Curve curve, double tolerance, out double[] polyline)
    {
        polyline = Array.Empty<double>();
        if (curve == null)
            return false;

        Polyline pl;
        if (!curve.TryGetPolyline(out pl) || !pl.IsValid || pl.Count < 2)
        {
            double chord = Math.Max(tolerance * 10.0, 1e-3);
            int segments = Math.Clamp((int)Math.Ceiling(curve.GetLength() / chord), 8, 1024);
            double[]? parameters = curve.DivideByCount(segments, includeEnds: true);
            if (parameters == null || parameters.Length < 2)
                return false;

            pl = new Polyline(parameters.Select(curve.PointAt));
        }

        int count = pl.Count;
        if (count < 2)
            return false;

        polyline = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            polyline[i * 2] = pl[i].X;
            polyline[i * 2 + 1] = pl[i].Y;
        }

        return true;
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
