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
        Vertices = vertices;
        VertexCount = vertexCount;
        Faces = faces;
        FaceCount = faceCount;
        CutVolume = cutVolume;
        FillVolume = fillVolume;
        DaylightVertices = daylightVertices;
        DaylightVertexCount = daylightVertexCount;
        OutputPolylines = outputPolylines ?? Array.Empty<OutputPolyline>();
        Diagnostics = diagnostics ?? structuredDiagnostics?.Select(static diagnostic => diagnostic.Message).ToArray() ?? Array.Empty<string>();
        StructuredDiagnostics = structuredDiagnostics ?? GradingDiagnostic.FromLegacyMessages(Diagnostics);
        PatchSummaries = patchSummaries ?? Array.Empty<GradingPatch>();
    }
}
