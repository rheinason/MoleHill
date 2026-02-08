namespace MoleHill.Core.Grading;

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

    public GradingResult(double[] vertices, int vertexCount,
                         int[] faces, int faceCount,
                         double cutVolume, double fillVolume,
                         double[] daylightVertices, int daylightVertexCount)
    {
        Vertices = vertices;
        VertexCount = vertexCount;
        Faces = faces;
        FaceCount = faceCount;
        CutVolume = cutVolume;
        FillVolume = fillVolume;
        DaylightVertices = daylightVertices;
        DaylightVertexCount = daylightVertexCount;
    }
}
