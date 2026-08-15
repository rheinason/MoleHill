using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class InSituStairReferenceBuilderTests
{
    [RhinoNativeFact]
    public void TryBuild_MixedSurfaceWinding_InterpretsEachSurfaceIndependently()
    {
        Mesh upwardSurface = CreateSlopedSurface(reverseWinding: false);
        Mesh downwardSurface = CreateSlopedSurface(reverseWinding: true);

        bool succeeded = InSituStairReferenceBuilder.TryBuild(
            new[] { upwardSurface, downwardSurface },
            riserHeight: 0.2,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0,
            out InSituStairBuildResult? result,
            out string? errorMessage);

        Assert.True(succeeded, errorMessage);
        Assert.NotNull(result);
        Assert.Equal(2, result!.References.Count);
        Assert.Equal(result.References[0].TreadDepth, result.References[1].TreadDepth, precision: 8);
        Assert.Equal(result.References[0].StepCount, result.References[1].StepCount);

        Assert.Equal(0, upwardSurface.Faces[0].A);
        Assert.Equal(1, upwardSurface.Faces[0].B);
        Assert.Equal(2, upwardSurface.Faces[0].C);
        Assert.Equal(0, downwardSurface.Faces[0].A);
        Assert.Equal(2, downwardSurface.Faces[0].B);
        Assert.Equal(1, downwardSurface.Faces[0].C);
    }

    private static Mesh CreateSlopedSurface(bool reverseWinding)
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(4.0, 0.0, 1.0);
        mesh.Vertices.Add(4.0, 2.0, 1.0);
        mesh.Vertices.Add(0.0, 2.0, 0.0);

        if (reverseWinding)
        {
            mesh.Faces.AddFace(0, 2, 1);
            mesh.Faces.AddFace(0, 3, 2);
        }
        else
        {
            mesh.Faces.AddFace(0, 1, 2);
            mesh.Faces.AddFace(0, 2, 3);
        }

        return mesh;
    }
}
