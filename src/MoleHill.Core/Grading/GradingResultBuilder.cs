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
        ValidateComponentArrays(outXy, originalZ, gradedZ, gradedVertices, vertexCount);
        ValidateFaceReferences(faces, faceCount, vertexCount);
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
        ValidateXyzArrays(originalVertices, gradedVertices, vertexCount);
        ValidateFaceReferences(faces, faceCount, vertexCount);
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
        int requiredVertexCount = ValidateFacePrefix(faces, faceCount, nameof(faces));
        ValidateDoubleArrayPrefix(outXy, requiredVertexCount, stride: 2, nameof(outXy));
        ValidateDoubleArrayPrefix(originalZ, requiredVertexCount, stride: 1, nameof(originalZ));
        ValidateDoubleArrayPrefix(gradedZ, requiredVertexCount, stride: 1, nameof(gradedZ));

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
        int requiredVertexCount = ValidateFacePrefix(faces, faceCount, nameof(faces));
        ValidateDoubleArrayPrefix(outXy, requiredVertexCount, stride: 2, nameof(outXy));
        ValidateDoubleArrayPrefix(originalZ, requiredVertexCount, stride: 1, nameof(originalZ));
        ValidateDoubleArrayPrefix(gradedZ, requiredVertexCount, stride: 1, nameof(gradedZ));

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
        int requiredVertexCount = ValidateFacePrefix(faces, faceCount, nameof(faces));
        ValidateDoubleArrayPrefix(originalVertices, requiredVertexCount, stride: 3, nameof(originalVertices));
        ValidateDoubleArrayPrefix(gradedVertices, requiredVertexCount, stride: 3, nameof(gradedVertices));

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
        int requiredVertexCount = ValidateFacePrefix(faces, faceCount, nameof(faces));
        ValidateDoubleArrayPrefix(originalVertices, requiredVertexCount, stride: 3, nameof(originalVertices));
        ValidateDoubleArrayPrefix(gradedVertices, requiredVertexCount, stride: 3, nameof(gradedVertices));

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
            faces,
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

    private static void ValidateFaceReferences(int[] faces, int faceCount, int vertexCount)
    {
        if (vertexCount < 0)
            throw new ArgumentOutOfRangeException(nameof(vertexCount), "Vertex count cannot be negative.");

        if (faces == null)
            throw new ArgumentNullException(nameof(faces));

        int requiredFaceValueCount = CheckedValueCount(faceCount, stride: 3, nameof(faceCount));
        if (faces.Length < requiredFaceValueCount)
            throw new ArgumentException("Face array is shorter than faceCount requires.", nameof(faces));

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            if ((uint)a >= (uint)vertexCount ||
                (uint)b >= (uint)vertexCount ||
                (uint)c >= (uint)vertexCount)
            {
                throw new ArgumentException("Face array references a vertex outside the result vertex range.", nameof(faces));
            }
        }
    }

    private static void ValidateComponentArrays(
        double[] outXy,
        double[] originalZ,
        double[] gradedZ,
        double[] gradedVertices,
        int vertexCount)
    {
        ValidateDoubleArrayPrefix(outXy, vertexCount, stride: 2, nameof(outXy));
        ValidateDoubleArrayPrefix(originalZ, vertexCount, stride: 1, nameof(originalZ));
        ValidateDoubleArrayPrefix(gradedZ, vertexCount, stride: 1, nameof(gradedZ));
        ValidateDoubleArrayPrefix(gradedVertices, vertexCount, stride: 3, nameof(gradedVertices));
    }

    private static void ValidateXyzArrays(
        double[] originalVertices,
        double[] gradedVertices,
        int vertexCount)
    {
        ValidateDoubleArrayPrefix(originalVertices, vertexCount, stride: 3, nameof(originalVertices));
        ValidateDoubleArrayPrefix(gradedVertices, vertexCount, stride: 3, nameof(gradedVertices));
    }

    private static void ValidateDoubleArrayPrefix(double[] values, int itemCount, int stride, string parameterName)
    {
        if (values == null)
            throw new ArgumentNullException(parameterName);

        int requiredValueCount = CheckedValueCount(itemCount, stride, parameterName);
        if (values.Length < requiredValueCount)
            throw new ArgumentException("Array is shorter than the declared item count requires.", parameterName);

        for (int i = 0; i < requiredValueCount; i++)
        {
            if (!double.IsFinite(values[i]))
                throw new ArgumentException("Array contains non-finite values.", parameterName);
        }
    }

    private static int ValidateFacePrefix(int[] faces, int faceCount, string parameterName)
    {
        if (faces == null)
            throw new ArgumentNullException(parameterName);

        int requiredFaceValueCount = CheckedValueCount(faceCount, stride: 3, parameterName);
        if (faces.Length < requiredFaceValueCount)
            throw new ArgumentException("Face array is shorter than faceCount requires.", parameterName);

        int maxVertexIndex = -1;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            if (a < 0 || b < 0 || c < 0)
                throw new ArgumentException("Face array references a negative vertex index.", parameterName);

            maxVertexIndex = Math.Max(maxVertexIndex, Math.Max(a, Math.Max(b, c)));
        }

        return maxVertexIndex + 1;
    }

    private static int CheckedValueCount(int itemCount, int stride, string parameterName)
    {
        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(parameterName, "Item count cannot be negative.");

        long valueCount = (long)itemCount * stride;
        if (valueCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "Item count is too large.");

        return (int)valueCount;
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
