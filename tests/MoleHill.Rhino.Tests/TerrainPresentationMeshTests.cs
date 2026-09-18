using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainPresentationMeshTests
{
    [RhinoNativeFact]
    public void CreateForDisplay_ModerateSlopeMeetingFlatGround_KeepsSmoothSeam()
    {
        using var mesh = Fold(0, 30);
        var shaded = TerrainPresentationMesh.CreateForDisplay(mesh);
        Assert.Same(mesh, shaded);
        Assert.Equal(6, mesh.Vertices.Count);
    }

    [RhinoNativeFact]
    public void CreateForDisplay_WallMeetingSlopingGround_SplitsOnlyPresentationCopy()
    {
        using var mesh = Fold(20, 75);
        var shaded = TerrainPresentationMesh.CreateForDisplay(mesh)!;
        Assert.NotSame(mesh, shaded);
        Assert.Equal(6, mesh.Vertices.Count);
        Assert.Equal(mesh.Faces.Count, shaded.Faces.Count);
        Assert.True(shaded.Vertices.Count > mesh.Vertices.Count);
        // Rhino's native UnweldEdge can round the shading copy to single-precision vertices.
        var originalBounds = mesh.GetBoundingBox(true);
        var shadedBounds = shaded.GetBoundingBox(true);
        Assert.True(originalBounds.Min.DistanceTo(shadedBounds.Min) < 1e-6);
        Assert.True(originalBounds.Max.DistanceTo(shadedBounds.Max) < 1e-6);
        var seamSlopes = Enumerable.Range(0, shaded.Vertices.Count)
            .Where(i => Math.Abs(shaded.Vertices[i].X) < 1e-6)
            .Select(i => Math.Acos(Math.Clamp(Math.Abs(shaded.Normals[i].Z), 0, 1)) * 180 / Math.PI).ToArray();
        Assert.Contains(seamSlopes, slope => Math.Abs(slope - 20) < 0.01);
        Assert.Contains(seamSlopes, slope => Math.Abs(slope - 75) < 0.01);
    }

    [RhinoNativeFact]
    public void CreateForDisplay_AlternatingTerrains_ReusesEachShadingCopy()
    {
        using var first = Fold(0, 80);
        using var second = Fold(10, 75);
        var a = TerrainPresentationMesh.CreateForDisplay(first);
        var b = TerrainPresentationMesh.CreateForDisplay(second);
        Assert.Same(a, TerrainPresentationMesh.CreateForDisplay(first));
        Assert.Same(b, TerrainPresentationMesh.CreateForDisplay(second));
    }

    [RhinoNativeFact]
    public void InvalidatePreviewBounds_AfterSculpt_DiscardsStaleShadingCopy()
    {
        using var mesh = Fold(0, 80);
        var state = new TerrainDisplayState { TerrainMesh = mesh, PreviewTerrainMesh = mesh };
        var before = TerrainPresentationMesh.CreateForDisplay(mesh);
        mesh.Vertices.SetVertex(4, 1, 0, 12);
        state.InvalidatePreviewBounds();
        var after = TerrainPresentationMesh.CreateForDisplay(mesh);
        Assert.NotSame(before, after);
        Assert.Equal(12, after!.GetBoundingBox(true).Max.Z);
    }

    private static Mesh Fold(double groundSlope, double wallSlope)
    {
        var mesh = new Mesh();
        double leftZ = -Math.Tan(groundSlope * Math.PI / 180);
        double rightZ = Math.Tan(wallSlope * Math.PI / 180);
        mesh.Vertices.Add(-1, 0, leftZ); mesh.Vertices.Add(-1, 1, leftZ);
        mesh.Vertices.Add(0, 0, 0); mesh.Vertices.Add(0, 1, 0);
        mesh.Vertices.Add(1, 0, rightZ); mesh.Vertices.Add(1, 1, rightZ);
        mesh.Faces.AddFace(0, 2, 3); mesh.Faces.AddFace(0, 3, 1);
        mesh.Faces.AddFace(2, 4, 5); mesh.Faces.AddFace(2, 5, 3);
        mesh.Normals.ComputeNormals();
        return mesh;
    }
}
