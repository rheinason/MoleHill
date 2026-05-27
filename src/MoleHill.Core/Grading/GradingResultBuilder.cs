namespace MoleHill.Core.Grading;

internal readonly record struct GradingVolumeMetrics(double CutVolume, double FillVolume);

internal static class GradingResultBuilder
{
    private const double DaylightThreshold = 0.001;

    public static GradingResult BuildFromComponents(
        double[] outXy,
        double[] originalZ,
        double[] gradedZ,
        double[] gradedVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<OutputPolyline>? outputPolylines = null,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<GradingPatch>? patchSummaries = null,
        IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        GradingVolumeMetrics volume = ComputeVolume(outXy, originalZ, gradedZ, faces, faceCount);
        double[] daylightVertices = BuildDaylightVertices(outXy, originalZ, gradedZ, faces, faceCount);

        return Create(
            gradedVertices,
            vertexCount,
            faces,
            faceCount,
            volume,
            daylightVertices,
            outputPolylines,
            diagnostics,
            patchSummaries,
            structuredDiagnostics);
    }

    public static GradingResult BuildFromXyz(
        double[] originalVertices,
        double[] gradedVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<OutputPolyline>? outputPolylines = null,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<GradingPatch>? patchSummaries = null,
        IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        GradingVolumeMetrics volume = ComputeVolume(originalVertices, gradedVertices, faces, faceCount);
        double[] daylightVertices = BuildDaylightVertices(originalVertices, gradedVertices, faces, faceCount);

        return Create(
            gradedVertices,
            vertexCount,
            faces,
            faceCount,
            volume,
            daylightVertices,
            outputPolylines,
            diagnostics,
            patchSummaries,
            structuredDiagnostics);
    }

    public static GradingVolumeMetrics ComputeVolume(
        double[] outXy,
        double[] originalZ,
        double[] gradedZ,
        int[] faces,
        int faceCount)
    {
        double cutVol = 0;
        double fillVol = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            double area2d = Math.Abs(
                (outXy[i1 * 2] - outXy[i0 * 2]) * (outXy[i2 * 2 + 1] - outXy[i0 * 2 + 1]) -
                (outXy[i2 * 2] - outXy[i0 * 2]) * (outXy[i1 * 2 + 1] - outXy[i0 * 2 + 1])) * 0.5;

            double dz0 = gradedZ[i0] - originalZ[i0];
            double dz1 = gradedZ[i1] - originalZ[i1];
            double dz2 = gradedZ[i2] - originalZ[i2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0)
                fillVol += vol;
            else
                cutVol += -vol;
        }

        return new GradingVolumeMetrics(cutVol, fillVol);
    }

    public static double[] BuildDaylightVertices(
        double[] outXy,
        double[] originalZ,
        double[] gradedZ,
        int[] faces,
        int faceCount)
    {
        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            AddDaylightEdge(i0, i1, outXy, originalZ, gradedZ, processedEdges, daylightPts);
            AddDaylightEdge(i1, i2, outXy, originalZ, gradedZ, processedEdges, daylightPts);
            AddDaylightEdge(i2, i0, outXy, originalZ, gradedZ, processedEdges, daylightPts);
        }

        return daylightPts.ToArray();
    }

    private static GradingVolumeMetrics ComputeVolume(
        double[] originalVertices,
        double[] gradedVertices,
        int[] faces,
        int faceCount)
    {
        double cutVol = 0;
        double fillVol = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            double area2d = Math.Abs(
                (gradedVertices[i1 * 3] - gradedVertices[i0 * 3]) * (gradedVertices[i2 * 3 + 1] - gradedVertices[i0 * 3 + 1]) -
                (gradedVertices[i2 * 3] - gradedVertices[i0 * 3]) * (gradedVertices[i1 * 3 + 1] - gradedVertices[i0 * 3 + 1]))
                * 0.5;

            double dz0 = gradedVertices[i0 * 3 + 2] - originalVertices[i0 * 3 + 2];
            double dz1 = gradedVertices[i1 * 3 + 2] - originalVertices[i1 * 3 + 2];
            double dz2 = gradedVertices[i2 * 3 + 2] - originalVertices[i2 * 3 + 2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0)
                fillVol += vol;
            else
                cutVol += -vol;
        }

        return new GradingVolumeMetrics(cutVol, fillVol);
    }

    private static double[] BuildDaylightVertices(
        double[] originalVertices,
        double[] gradedVertices,
        int[] faces,
        int faceCount)
    {
        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            AddDaylightEdge(i0, i1, originalVertices, gradedVertices, processedEdges, daylightPts);
            AddDaylightEdge(i1, i2, originalVertices, gradedVertices, processedEdges, daylightPts);
            AddDaylightEdge(i2, i0, originalVertices, gradedVertices, processedEdges, daylightPts);
        }

        return daylightPts.ToArray();
    }

    private static GradingResult Create(
        double[] gradedVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradingVolumeMetrics volume,
        double[] daylightVertices,
        IReadOnlyList<OutputPolyline>? outputPolylines,
        IReadOnlyList<string>? diagnostics,
        IReadOnlyList<GradingPatch>? patchSummaries,
        IReadOnlyList<GradingDiagnostic>? structuredDiagnostics)
    {
        return new GradingResult(
            gradedVertices,
            vertexCount,
            (int[])faces.Clone(),
            faceCount,
            volume.CutVolume,
            volume.FillVolume,
            daylightVertices,
            daylightVertices.Length / 3,
            outputPolylines,
            diagnostics,
            patchSummaries,
            structuredDiagnostics);
    }

    private static void AddDaylightEdge(
        int a,
        int b,
        double[] xy,
        double[] originalZ,
        double[] gradedZ,
        HashSet<long> processed,
        List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key))
            return;

        double dzA = gradedZ[a] - originalZ[a];
        double dzB = gradedZ[b] - originalZ[b];

        if ((dzA > DaylightThreshold && dzB < -DaylightThreshold) || (dzA < -DaylightThreshold && dzB > DaylightThreshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(xy[a * 2] + t * (xy[b * 2] - xy[a * 2]));
            pts.Add(xy[a * 2 + 1] + t * (xy[b * 2 + 1] - xy[a * 2 + 1]));
            pts.Add(gradedZ[a] + t * (gradedZ[b] - gradedZ[a]));
        }
        else if (Math.Abs(dzA) <= DaylightThreshold && Math.Abs(dzB) > DaylightThreshold)
        {
            pts.Add(xy[a * 2]);
            pts.Add(xy[a * 2 + 1]);
            pts.Add(gradedZ[a]);
        }
        else if (Math.Abs(dzB) <= DaylightThreshold && Math.Abs(dzA) > DaylightThreshold)
        {
            pts.Add(xy[b * 2]);
            pts.Add(xy[b * 2 + 1]);
            pts.Add(gradedZ[b]);
        }
    }

    private static void AddDaylightEdge(
        int a,
        int b,
        double[] originalVertices,
        double[] gradedVertices,
        HashSet<long> processed,
        List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key))
            return;

        double dzA = gradedVertices[a * 3 + 2] - originalVertices[a * 3 + 2];
        double dzB = gradedVertices[b * 3 + 2] - originalVertices[b * 3 + 2];

        if ((dzA > DaylightThreshold && dzB < -DaylightThreshold) || (dzA < -DaylightThreshold && dzB > DaylightThreshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(gradedVertices[a * 3] + t * (gradedVertices[b * 3] - gradedVertices[a * 3]));
            pts.Add(gradedVertices[a * 3 + 1] + t * (gradedVertices[b * 3 + 1] - gradedVertices[a * 3 + 1]));
            pts.Add(gradedVertices[a * 3 + 2] + t * (gradedVertices[b * 3 + 2] - gradedVertices[a * 3 + 2]));
        }
        else if (Math.Abs(dzA) <= DaylightThreshold && Math.Abs(dzB) > DaylightThreshold)
        {
            pts.Add(gradedVertices[a * 3]);
            pts.Add(gradedVertices[a * 3 + 1]);
            pts.Add(gradedVertices[a * 3 + 2]);
        }
        else if (Math.Abs(dzB) <= DaylightThreshold && Math.Abs(dzA) > DaylightThreshold)
        {
            pts.Add(gradedVertices[b * 3]);
            pts.Add(gradedVertices[b * 3 + 1]);
            pts.Add(gradedVertices[b * 3 + 2]);
        }
    }
}
