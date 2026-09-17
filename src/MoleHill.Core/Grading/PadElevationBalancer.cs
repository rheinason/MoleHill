// Applies bracketed net-volume search to one translated planar Grade Pad boundary.
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
        double[] terrainVertices,
        int terrainVertexCount,
        int[] terrainFaces,
        int terrainFaceCount,
        PadGrader.PadBoundary pad,
        PadGrader.LockCurve[]? locks,
        double minimumElevation,
        double maximumElevation,
        double targetNet,
        double volumeTolerance,
        int iterationCap = 24,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        ArgumentNullException.ThrowIfNull(pad);
        GradingResult? bestGrading = null;
        double bestError = double.PositiveInfinity;
        double originalElevation = pad.BoundaryVertices[2];

        VolumeSearchSample? Evaluate(double elevation)
        {
            PadGrader.PadBoundary adjusted = Translate(pad, elevation - originalElevation);
            GradingResult? result = PadGrader.Grade(
                terrainVertices, terrainVertexCount,
                terrainFaces, terrainFaceCount,
                new[] { adjusted }, locks, out string? error, modelTolerance);
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
