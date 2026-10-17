// Balances several Grade Pad boundaries at once: one shared offset for the set, or each pad to its own net.
using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

/// <summary>How <see cref="PadSetBalancer"/> moves the pads it balances.</summary>
public enum PadBalanceMode
{
    /// <summary>Every pad moves by one shared amount until the set's total net meets the target.</summary>
    Together,

    /// <summary>Each pad moves on its own until its own net meets the target.</summary>
    Each
}

public sealed class PadSetBalanceResult
{
    /// <summary>"Converged" when every balanced quantity meets its tolerance, else why it did not.</summary>
    public required string Status { get; init; }
    public string? Diagnostic { get; init; }

    /// <summary>The elevation change found for each moving pad, in input order.</summary>
    public required double[] Offsets { get; init; }

    /// <summary>Each moving pad's own cut and fill at <see cref="Offsets"/> (see <see cref="PadSetBalancer"/> for how volume is shared).</summary>
    public required double[] PadCut { get; init; }
    public required double[] PadFill { get; init; }

    public double Cut => PadCut.Sum();
    public double Fill => PadFill.Sum();
    public double Net => Cut - Fill;

    /// <summary>How many times the pads were graded.</summary>
    public int GradeCount { get; init; }

    public IReadOnlyList<VolumeSearchResult> Searches { get; init; } = Array.Empty<VolumeSearchResult>();
}

/// <summary>
/// Finds elevations for a set of planar Grade Pad boundaries so that their cut and fill balance. All pads
/// (the moving ones and any <c>fixedPads</c>, such as a card's other pads) are graded together, as the
/// card grades them, so batters that meet are measured as built. Pass the card's locks, breaklines,
/// tolerance and detail size too, or the grade measured here is not the one the card builds: a breakline
/// inside a batter's reach stops it.
///
/// <para>Volume comes from <see cref="SurfaceDifferenceVolume"/>. Where pads share ground, a graded face
/// belongs to the pad whose footprint is nearest its centre (inside a footprint is distance zero), so each
/// pad's batters count towards it and merged batters split down the middle. A fixed pad's volume is graded
/// but counts towards no moving pad.</para>
///
/// <para><see cref="PadBalanceMode.Together"/> is one bracketed search on a shared offset.
/// <see cref="PadBalanceMode.Each"/> balances the pads one at a time with the others held, and repeats the
/// sweep until every pad holds its target at once: pads whose batters do not meet are independent and settle
/// in one sweep; neighbours that share ground take a sweep or two more.</para>
/// </summary>
public static class PadSetBalancer
{
    private sealed record Measurement(GradingResult Grading, double[] PadCut, double[] PadFill, bool Fallback, string? Error);

    public static PadSetBalanceResult Balance(
        IndexedTriMesh terrain,
        IReadOnlyList<PadGrader.PadBoundary> movingPads,
        IReadOnlyList<PadGrader.PadBoundary>? fixedPads,
        PadGrader.LockCurve[]? locks,
        PadBalanceMode mode,
        double targetNet,
        IReadOnlyList<double> volumeTolerances,
        int iterationCap = 24,
        int sweepCap = 6,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        Func<bool>? shouldCancel = null,
        Action<int>? progress = null,
        IReadOnlyList<ConstraintPolyline>? hardConstraints = null,
        double terrainDetailSize = 0.0)
    {
        ArgumentNullException.ThrowIfNull(movingPads);
        ArgumentNullException.ThrowIfNull(volumeTolerances);
        int count = movingPads.Count;
        if (count == 0)
            throw new ArgumentException("No pads to balance.", nameof(movingPads));
        if (volumeTolerances.Count != count)
            throw new ArgumentException("One volume tolerance is needed per moving pad.", nameof(volumeTolerances));

        PadGrader.PadBoundary[] held = fixedPads?.ToArray() ?? Array.Empty<PadGrader.PadBoundary>();
        var reference = new ReferenceSurface(terrain);
        var lowOffset = new double[count];
        var highOffset = new double[count];
        for (int k = 0; k < count; k++)
        {
            if (!PadElevationBalancer.TrySuggestRange(terrain, movingPads[k], out double low, out double high))
                throw new ArgumentException($"Pad {k + 1} does not lie over the terrain.", nameof(movingPads));
            double z = movingPads[k].BoundaryVertices[2];
            lowOffset[k] = low - z;
            highOffset[k] = high - z;
        }

        int grades = 0;
        Measurement? Grade(double[] offsets)
        {
            if (shouldCancel?.Invoke() == true)
                throw new OperationCanceledException("Pad balance cancelled.");
            var pads = new PadGrader.PadBoundary[count + held.Length];
            for (int k = 0; k < count; k++)
                pads[k] = offsets[k] == 0.0 ? movingPads[k] : PadElevationBalancer.Translate(movingPads[k], offsets[k]);
            Array.Copy(held, 0, pads, count, held.Length);

            // The windowed entry point is the one the Grade Pad card builds with. A balance measured on
            // geometry the card does not build is not a balance, so measure what it builds.
            GradeOutcome outcome = PadGrader.GradeWindowed(new PadGradeRequest
            {
                Terrain = terrain,
                Pads = pads,
                LockCurves = locks ?? Array.Empty<PadGrader.LockCurve>(),
                HardConstraints = hardConstraints ?? Array.Empty<ConstraintPolyline>(),
                ModelTolerance = modelTolerance,
                TerrainDetailSize = terrainDetailSize,
            }, previous: null, next: new GradingWindows.Memo(), notes: new List<string>());
            grades++;
            progress?.Invoke(grades);
            if (outcome.Result is not { } result)
                return null;

            var faceCut = new double[result.FaceCount];
            var faceFill = new double[result.FaceCount];
            SurfaceDifferenceVolume.Measure(
                result.Vertices, result.Faces, result.FaceCount, reference, modelTolerance,
                faceCut: faceCut, faceFill: faceFill, shouldCancel: shouldCancel);
            var padCut = new double[count];
            var padFill = new double[count];
            Attribute(result, faceCut, faceFill, pads, count, padCut, padFill);
            bool fallback = result.StructuredDiagnostics.Any(diagnostic =>
                diagnostic.Code.EndsWith(".fallback", StringComparison.OrdinalIgnoreCase));
            return new Measurement(result, padCut, padFill, fallback,
                fallback ? outcome.ErrorMessage ?? "Grade Pad used a fallback tier." : outcome.ErrorMessage);
        }

        return mode == PadBalanceMode.Together
            ? BalanceTogether(Grade, count, lowOffset, highOffset, targetNet, volumeTolerances.Sum(), iterationCap, () => grades)
            : BalanceEach(Grade, count, lowOffset, highOffset, targetNet, volumeTolerances, iterationCap, sweepCap, modelTolerance, () => grades);
    }

    private static PadSetBalanceResult BalanceTogether(
        Func<double[], Measurement?> grade,
        int count,
        double[] lowOffset,
        double[] highOffset,
        double targetNet,
        double tolerance,
        int iterationCap,
        Func<int> grades)
    {
        // The shared offset brackets every pad: at the lowest, all of them can only cut; at the highest, only fill.
        double low = lowOffset.Min(), high = highOffset.Max();
        var measured = new Dictionary<double, Measurement>();
        VolumeSearchResult search = BracketedVolumeSearch.Search(offset =>
        {
            var offsets = new double[count];
            Array.Fill(offsets, offset);
            Measurement? measurement = grade(offsets);
            if (measurement == null)
                return null;
            measured[offset] = measurement;
            return new VolumeSearchSample(offset, measurement.PadCut.Sum(), measurement.PadFill.Sum(),
                measurement.Fallback, measurement.Error);
        }, low, high, targetNet, tolerance, iterationCap);

        if (search.Best is not { } best || !measured.TryGetValue(best.Elevation, out Measurement? final))
        {
            return new PadSetBalanceResult
            {
                Status = search.Status,
                Diagnostic = search.Diagnostic ?? "No grade completed, so no balance was measured.",
                Offsets = new double[count],
                PadCut = new double[count],
                PadFill = new double[count],
                GradeCount = grades(),
                Searches = new[] { search }
            };
        }

        var chosen = new double[count];
        Array.Fill(chosen, best.Elevation);
        bool met = Math.Abs(best.Net - targetNet) <= tolerance;
        return new PadSetBalanceResult
        {
            Status = met ? "Converged" : search.Status,
            Diagnostic = met ? null : search.Diagnostic,
            Offsets = chosen,
            PadCut = final.PadCut,
            PadFill = final.PadFill,
            GradeCount = grades(),
            Searches = new[] { search }
        };
    }

    private static PadSetBalanceResult BalanceEach(
        Func<double[], Measurement?> grade,
        int count,
        double[] lowOffset,
        double[] highOffset,
        double targetNet,
        IReadOnlyList<double> tolerances,
        int iterationCap,
        int sweepCap,
        double modelTolerance,
        Func<int> grades)
    {
        var offsets = new double[count];
        var searches = new List<VolumeSearchResult>();
        Measurement? final = null;
        string? lastDiagnostic = null;

        bool settled = false;
        for (int sweep = 0; sweep < Math.Max(1, sweepCap); sweep++)
        {
            var before = (double[])offsets.Clone();
            for (int k = 0; k < count; k++)
            {
                int pad = k;
                Measurement? bestMeasurement = null;
                double bestError = double.PositiveInfinity;
                VolumeSearchResult search = BracketedVolumeSearch.Search(offset =>
                {
                    var trial = (double[])offsets.Clone();
                    trial[pad] = offset;
                    Measurement? measurement = grade(trial);
                    if (measurement == null)
                        return null;
                    double error = Math.Abs(measurement.PadCut[pad] - measurement.PadFill[pad] - targetNet);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestMeasurement = measurement;
                    }

                    return new VolumeSearchSample(offset, measurement.PadCut[pad], measurement.PadFill[pad],
                        measurement.Fallback, measurement.Error);
                }, lowOffset[pad], highOffset[pad], targetNet, tolerances[pad], iterationCap);
                searches.Add(search);

                if (search.Best is not { } best)
                {
                    return new PadSetBalanceResult
                    {
                        Status = search.Status,
                        Diagnostic = $"Pad {pad + 1}: {search.Diagnostic ?? "no grade completed."}",
                        Offsets = offsets,
                        PadCut = final?.PadCut ?? new double[count],
                        PadFill = final?.PadFill ?? new double[count],
                        GradeCount = grades(),
                        Searches = searches
                    };
                }

                offsets[pad] = best.Elevation;
                final = bestMeasurement;
                if (search.Status != "Converged")
                    lastDiagnostic = $"Pad {pad + 1}: {search.Diagnostic ?? search.Status}";
            }

            // The last search measured the last pad at its chosen offset with every other pad already moved,
            // so that grade is the whole set as it stands; with one pad there is nothing else to check.
            if (final == null)
                break;
            if (AllMet(final, count, targetNet, tolerances))
            {
                return new PadSetBalanceResult
                {
                    Status = "Converged",
                    Offsets = offsets,
                    PadCut = final.PadCut,
                    PadFill = final.PadFill,
                    GradeCount = grades(),
                    Searches = searches
                };
            }

            if (count == 1)
                break;

            // Another sweep only helps while the pads are still moving one another. When no pad moved by
            // more than the model tolerance, the one that misses cannot reach its target at any elevation
            // (its volume steps past it), and sweeping again would only repeat the same searches.
            double moved = 0.0;
            for (int k = 0; k < count; k++)
                moved = Math.Max(moved, Math.Abs(offsets[k] - before[k]));
            if (sweep > 0 && moved <= modelTolerance)
            {
                settled = true;
                break;
            }
        }

        string? missed = final == null ? null : string.Join(", ", Enumerable.Range(0, count)
            .Where(k => Math.Abs(final.PadCut[k] - final.PadFill[k] - targetNet) > tolerances[k])
            .Select(k => $"pad {k + 1}"));
        return new PadSetBalanceResult
        {
            Status = count == 1 ? searches[^1].Status : settled ? "Settled" : "SweepCap",
            Diagnostic = count == 1
                ? searches[^1].Diagnostic
                : settled
                    ? $"The pads settled, but {missed} cannot meet the target (the volume steps past it between two elevations a hair apart), so the closest elevations measured were kept. {lastDiagnostic}"
                    : $"The pads share ground and were still moving one another after {sweepCap} sweeps; the closest set measured was kept. {lastDiagnostic}",
            Offsets = offsets,
            PadCut = final?.PadCut ?? new double[count],
            PadFill = final?.PadFill ?? new double[count],
            GradeCount = grades(),
            Searches = searches
        };
    }

    private static bool AllMet(Measurement measurement, int count, double targetNet, IReadOnlyList<double> tolerances)
    {
        for (int k = 0; k < count; k++)
        {
            if (Math.Abs(measurement.PadCut[k] - measurement.PadFill[k] - targetNet) > tolerances[k])
                return false;
        }

        return true;
    }

    /// <summary>Gives each face's volume to the pad whose footprint is nearest the face's centre.</summary>
    internal static void Attribute(
        GradingResult result,
        double[] faceCut,
        double[] faceFill,
        PadGrader.PadBoundary[] pads,
        int movingCount,
        double[] padCut,
        double[] padFill)
    {
        double[] v = result.Vertices;
        for (int face = 0; face < result.FaceCount; face++)
        {
            if (faceCut[face] == 0.0 && faceFill[face] == 0.0)
                continue;
            int a = result.Faces[face * 3], b = result.Faces[face * 3 + 1], c = result.Faces[face * 3 + 2];
            double x = (v[a * 3] + v[b * 3] + v[c * 3]) / 3.0;
            double y = (v[a * 3 + 1] + v[b * 3 + 1] + v[c * 3 + 1]) / 3.0;
            int owner = 0;
            double nearest = double.PositiveInfinity;
            for (int p = 0; p < pads.Length; p++)
            {
                double distance = Geometry2D.PointInPolygon(x, y, pads[p].XyVertices, pads[p].VertexCount)
                    ? 0.0
                    : Geometry2D.DistanceToPolygon(x, y, pads[p].XyVertices, pads[p].VertexCount);
                if (distance < nearest)
                {
                    nearest = distance;
                    owner = p;
                }
            }

            if (owner >= movingCount)
                continue;
            padCut[owner] += faceCut[face];
            padFill[owner] += faceFill[face];
        }
    }
}
