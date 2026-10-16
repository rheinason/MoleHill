// The meshes a drape reads heights from, indexed once so each station costs a grid lookup.
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Every mesh, surface or polysurface a drape was pointed at, each behind its own
/// <see cref="MeshHeightProjector"/>. A station reads the top (or bottom) surface across all of them, so
/// a road slab lying on a terrain mesh drapes onto the slab where it exists and the terrain elsewhere.
///
/// <para>Rhino's mesh/line intersection tests every face of a mesh, which over a dense terrain cost tens of
/// milliseconds a station, and the edge search samples a station many times over.</para>
/// </summary>
internal sealed class DrapeTargets
{
    private readonly List<MeshHeightProjector> _projectors = new();
    private readonly double _tolerance;

    private DrapeTargets(double tolerance) => _tolerance = tolerance;

    public int Count => _projectors.Count;

    /// <summary>Indexes each mesh; one that cannot be read is skipped and counted.</summary>
    public static DrapeTargets Create(IEnumerable<Mesh> meshes, double tolerance, out int unreadable)
    {
        var targets = new DrapeTargets(tolerance);
        unreadable = 0;
        foreach (Mesh mesh in meshes)
        {
            if (!RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh extracted, out _))
            {
                unreadable++;
                continue;
            }

            (double[] vertices, int vertexCount, int[] faces, int faceCount) = extracted;
            targets._projectors.Add(new MeshHeightProjector(vertices, vertexCount, faces, faceCount));
        }

        return targets;
    }

    /// <summary>The highest (or lowest) surface over a plan point across every target; null where none is.</summary>
    public double? HeightAt(double x, double y, bool lowest)
    {
        double? best = null;
        foreach (MeshHeightProjector projector in _projectors)
        {
            if (!projector.TryGetHeightExtremes(x, y, _tolerance, out double low, out double high))
                continue;
            double z = lowest ? low : high;
            if (best is not { } current || (lowest ? z < current : z > current))
                best = z;
        }

        return best;
    }
}
