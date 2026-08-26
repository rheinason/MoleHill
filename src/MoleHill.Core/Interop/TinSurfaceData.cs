namespace MoleHill.Core.Interop;

public sealed class TinSurfaceData
{
    public List<TinSurfacePoint> Points { get; } = new();
    public List<TinSurfaceTriangle> Triangles { get; } = new();
    public string Name { get; set; } = "Surface";
}

public readonly record struct TinSurfacePoint(int Id, double X, double Y, double Z);

public readonly record struct TinSurfaceTriangle(int A, int B, int C);
