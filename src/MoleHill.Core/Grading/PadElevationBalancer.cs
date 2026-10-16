// Applies bracketed net-volume search to one translated planar Grade Pad boundary.
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public sealed class PadElevationBalanceResult
{
    public required VolumeSearchResult Search { get; init; }
    public GradingResult? BestGrading { get; init; }
    public double[]? AdjustedBoundaryVertices { get; init; }
}

public static class PadElevationBalancer
{
    public static PadElevationBalanceResult Balance(
        IndexedTriMesh terrain,
        PadGrader.PadBoundary pad,
        PadGrader.LockCurve[]? locks,
        double minimumElevation,
        double maximumElevation,
        double targetNet,
        double volumeTolerance,
        int iterationCap = 24,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        (double[] terrainVertices, int terrainVertexCount, int[] terrainFaces, int terrainFaceCount) = terrain;
        ArgumentNullException.ThrowIfNull(pad);
        GradingResult? bestGrading = null;
        double bestError = double.PositiveInfinity;
        double originalElevation = pad.BoundaryVertices[2];

        VolumeSearchSample? Evaluate(double elevation)
        {
            PadGrader.PadBoundary adjusted = Translate(pad, elevation - originalElevation);
            GradeOutcome gradeOutcome = PadGrader.Grade(new PadGradeRequest
            {
                Terrain = new IndexedTriMesh(terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount),
                Pads = new[] { adjusted },
                LockCurves = locks ?? Array.Empty<PadGrader.LockCurve>(),
                ModelTolerance = modelTolerance,
            });
            string? error = gradeOutcome.ErrorMessage;
            GradingResult? result = gradeOutcome.Result;
            if (result == null)
                return null;
            double errorMagnitude = Math.Abs(result.NetVolume - targetNet);
            if (errorMagnitude < bestError)
            {
                bestError = errorMagnitude;
                bestGrading = result;
            }
            bool fallback = result.StructuredDiagnostics.Any(diagnostic =>
                diagnostic.Code.EndsWith(".fallback", StringComparison.OrdinalIgnoreCase));
            return new VolumeSearchSample(
                elevation, result.CutVolume, result.FillVolume, fallback,
                fallback ? error ?? "Grade Pad used a fallback tier." : error);
        }

        VolumeSearchResult search = BracketedVolumeSearch.Search(
            Evaluate, minimumElevation, maximumElevation, targetNet, volumeTolerance, iterationCap);
        double[]? boundary = search.Best is { } chosen
            ? Translate(pad, chosen.Elevation - originalElevation).BoundaryVertices
            : null;
        return new PadElevationBalanceResult
        {
            Search = search,
            BestGrading = bestGrading,
            AdjustedBoundaryVertices = boundary
        };
    }

    /// <summary>
    /// The elevation range (of the first boundary vertex, as <see cref="Balance"/> takes it) that must
    /// contain the balance: at its bottom the whole pad sits at or below the terrain under it, so the pad
    /// can only cut; at its top it sits at or above, so it can only fill. The terrain is read at every
    /// terrain vertex inside the footprint and at the boundary's own vertices, and the range is widened by
    /// <paramref name="marginFraction"/> of its span so the batters, which reach beyond the footprint, are
    /// bracketed too. False when no terrain lies under the pad.
    /// </summary>
    public static bool TrySuggestRange(
        IndexedTriMesh terrain,
        PadGrader.PadBoundary pad,
        out double minimumElevation,
        out double maximumElevation,
        double marginFraction = 0.25)
    {
        ArgumentNullException.ThrowIfNull(pad);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = terrain;
        minimumElevation = double.NaN;
        maximumElevation = double.NaN;
        double lowest = double.PositiveInfinity;
        double highest = double.NegativeInfinity;

        void Observe(double x, double y, double z)
        {
            double offset = z - pad.EvaluateZ(x, y);
            lowest = Math.Min(lowest, offset);
            highest = Math.Max(highest, offset);
        }

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int index = 0; index < pad.VertexCount; index++)
        {
            minX = Math.Min(minX, pad.XyVertices[index * 2]);
            maxX = Math.Max(maxX, pad.XyVertices[index * 2]);
            minY = Math.Min(minY, pad.XyVertices[index * 2 + 1]);
            maxY = Math.Max(maxY, pad.XyVertices[index * 2 + 1]);
        }

        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            double x = vertices[vertex * 3], y = vertices[vertex * 3 + 1];
            if (x < minX || x > maxX || y < minY || y > maxY)
                continue;
            if (Geometry.Geometry2D.PointInPolygon(x, y, pad.XyVertices, pad.VertexCount))
                Observe(x, y, vertices[vertex * 3 + 2]);
        }

        var projector = new Analysis.MeshHeightProjector(vertices, vertexCount, faces, faceCount);
        for (int index = 0; index < pad.VertexCount; index++)
        {
            double x = pad.XyVertices[index * 2], y = pad.XyVertices[index * 2 + 1];
            if (projector.TryProjectZ(x, y, pad.EvaluateZ(x, y), GradingTolerances.DefaultModelTolerance,
                    out double z, out _))
                Observe(x, y, z);
        }

        if (!double.IsFinite(lowest) || !double.IsFinite(highest))
            return false;

        // A pad already lying on flat ground has no span; a unit of slack keeps the bracket open.
        double span = Math.Max(highest - lowest, 1e-6);
        double margin = span * Math.Max(0.0, marginFraction) + 1e-6;
        // Translating the pad by d moves its plane by d, so it clears the terrain once d passes an offset.
        double firstZ = pad.BoundaryVertices[2];
        minimumElevation = firstZ + lowest - margin;
        maximumElevation = firstZ + highest + margin;
        return true;
    }

    private static PadGrader.PadBoundary Translate(PadGrader.PadBoundary pad, double elevationDelta)
    {
        double[] vertices = (double[])pad.BoundaryVertices.Clone();
        for (int index = 0; index < pad.VertexCount; index++)
            vertices[index * 3 + 2] += elevationDelta;
        return PadGrader.PadBoundary.CreatePlanar(
            vertices, pad.VertexCount,
            pad.PlaneXCoeff, pad.PlaneYCoeff, pad.PlaneConstant + elevationDelta,
            pad.SlopeAngleDeg, pad.MaxDistance, pad.CornerFanSegments,
            pad.StitchApronDistance, pad.FillSlopeAngleDeg);
    }
}
