namespace MoleHill.Core.Grading;

/// <summary>
/// A graded 3-D polyline (flat XYZ) that is part of a grading result —
/// e.g. a road edge or a pad boundary curve.
/// </summary>
public sealed class OutputPolyline
{
    /// <summary>Flat XYZ vertices: [x0,y0,z0, x1,y1,z1, …]</summary>
    public double[] Vertices { get; }

    /// <summary>Number of vertices.</summary>
    public int VertexCount { get; }

    /// <summary>True if the polyline forms a closed loop (first vertex == last vertex logically).</summary>
    public bool IsClosed { get; }

    public OutputPolyline(double[] vertices, int vertexCount, bool isClosed = false)
    {
        Vertices = vertices;
        VertexCount = vertexCount;
        IsClosed = isClosed;
    }
}

/// <summary>
/// Result of a grading operation (pad, path, surface).
/// Contains the modified mesh and volume metrics.
/// </summary>
public sealed class GradingResult
{
    /// <summary>Flat XYZ vertices: [x0,y0,z0, x1,y1,z1, …]</summary>
    public double[] Vertices { get; }

    /// <summary>Number of vertices.</summary>
    public int VertexCount { get; }

    /// <summary>Triangle face indices: [i0,i1,i2, …]</summary>
    public int[] Faces { get; }

    /// <summary>Number of faces.</summary>
    public int FaceCount { get; }

    /// <summary>Total excavation volume (positive).</summary>
    public double CutVolume { get; }

    /// <summary>Total embankment volume (positive).</summary>
    public double FillVolume { get; }

    /// <summary>Net volume: Cut - Fill (positive = net cut).</summary>
    public double NetVolume => CutVolume - FillVolume;

    /// <summary>Daylight line vertices (flat XYZ), or empty if none.</summary>
    public double[] DaylightVertices { get; }

    /// <summary>Number of daylight line vertices.</summary>
    public int DaylightVertexCount { get; }

    /// <summary>
    /// Graded polylines derived from the operation: road edges for Grade Path,
    /// pad boundary curves for Grade Pad. Each polyline carries graded Z values.
    /// </summary>
    public IReadOnlyList<OutputPolyline> OutputPolylines { get; }

    /// <summary>Operation-specific diagnostics.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Structured operation diagnostics for UI filtering, conflict display, and downstream workflow decisions.</summary>
    public IReadOnlyList<GradingDiagnostic> StructuredDiagnostics { get; }

    /// <summary>Internal patch ownership/seam summaries for downstream runtime caching.</summary>
    internal IReadOnlyList<GradingPatch> PatchSummaries { get; }

    internal GradingResult(double[] vertices, int vertexCount,
                           int[] faces, int faceCount,
                           double cutVolume, double fillVolume,
                           double[] daylightVertices, int daylightVertexCount,
                            IReadOnlyList<OutputPolyline>? outputPolylines = null,
                            IReadOnlyList<string>? diagnostics = null,
                            IReadOnlyList<GradingPatch>? patchSummaries = null,
                            IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        Vertices = CopyDoublePrefix(vertices, vertexCount, stride: 3, nameof(vertices));
        VertexCount = vertexCount;
        Faces = CopyIntPrefix(faces, faceCount, stride: 3, nameof(faces));
        FaceCount = faceCount;
        ValidateFiniteValues(Vertices, nameof(vertices));
        ValidateFaceIndices(Faces, faceCount, vertexCount, nameof(faces));
        CutVolume = cutVolume;
        FillVolume = fillVolume;
        DaylightVertices = CopyDoublePrefix(daylightVertices, daylightVertexCount, stride: 3, nameof(daylightVertices));
        DaylightVertexCount = daylightVertexCount;
        ValidateFiniteValues(DaylightVertices, nameof(daylightVertices));
        OutputPolylines = outputPolylines ?? Array.Empty<OutputPolyline>();
        Diagnostics = diagnostics ?? structuredDiagnostics?.Select(static diagnostic => diagnostic.Message).ToArray() ?? Array.Empty<string>();
        StructuredDiagnostics = structuredDiagnostics ?? GradingDiagnostic.FromLegacyMessages(Diagnostics);
        PatchSummaries = patchSummaries ?? Array.Empty<GradingPatch>();
    }

    private static double[] CopyDoublePrefix(double[] values, int itemCount, int stride, string parameterName)
    {
        int valueCount = CheckedValueCount(itemCount, stride, parameterName);
        if (values.Length < valueCount)
            throw new ArgumentException("Array is shorter than the declared item count requires.", parameterName);
        if (values.Length == valueCount)
            return (double[])values.Clone();

        var copy = new double[valueCount];
        Array.Copy(values, copy, valueCount);
        return copy;
    }

    private static int[] CopyIntPrefix(int[] values, int itemCount, int stride, string parameterName)
    {
        int valueCount = CheckedValueCount(itemCount, stride, parameterName);
        if (values.Length < valueCount)
            throw new ArgumentException("Array is shorter than the declared item count requires.", parameterName);
        if (values.Length == valueCount)
            return (int[])values.Clone();

        var copy = new int[valueCount];
        Array.Copy(values, copy, valueCount);
        return copy;
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

    private static void ValidateFiniteValues(double[] values, string parameterName)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
                throw new ArgumentException("Array contains non-finite values.", parameterName);
        }
    }

    private static void ValidateFaceIndices(int[] faces, int faceCount, int vertexCount, string parameterName)
    {
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            if ((uint)a >= (uint)vertexCount ||
                (uint)b >= (uint)vertexCount ||
                (uint)c >= (uint)vertexCount)
            {
                throw new ArgumentException("Face array references a vertex outside the result vertex range.", parameterName);
            }
        }
    }
}
