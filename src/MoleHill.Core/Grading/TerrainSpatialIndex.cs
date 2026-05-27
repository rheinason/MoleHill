using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal sealed class TerrainSpatialIndex
{
    public TerrainSpatialIndex(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        Vertices = vertices;
        VertexCount = vertexCount;
        Faces = faces;
        FaceCount = faceCount;
        FaceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        Bounds = ComputeBounds(vertices, vertexCount);
        HasBoundaryLoop = MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(vertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        BoundaryLoopXy = boundaryLoop;
        BoundaryVertexCount = boundaryVertexCount;
    }

    public double[] Vertices { get; }

    public int VertexCount { get; }

    public int[] Faces { get; }

    public int FaceCount { get; }

    public TerrainFaceGrid FaceGrid { get; }

    public Bounds2D Bounds { get; }

    public bool HasBoundaryLoop { get; }

    public double[] BoundaryLoopXy { get; }

    public int BoundaryVertexCount { get; }

    public double InterpolateZ(double x, double y) => FaceGrid.InterpolateZ(x, y);

    private static Bounds2D ComputeBounds(double[] vertices, int vertexCount)
    {
        if (vertexCount <= 0)
            return new Bounds2D(0.0, 0.0, 0.0, 0.0);

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return new Bounds2D(minX, maxX, minY, maxY);
    }
}
