using Rhino.Geometry;

namespace MoleHill.Grasshopper.Utilities;

internal static class GeometryDisposal
{
    public static void DisposeCurves(IEnumerable<Curve> curves)
    {
        foreach (Curve curve in curves)
            curve.Dispose();
    }
}
