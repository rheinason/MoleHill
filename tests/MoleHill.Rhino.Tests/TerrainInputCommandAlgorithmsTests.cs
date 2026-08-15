using Rhino.DocObjects;
using Rhino.Geometry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class TerrainInputCommandAlgorithmsTests
{
    [RhinoNativeFact]
    public void PrepareValidation_RemoveDuplicateObjects_KeepsOneCopy()
    {
        using var first = new PolylineCurve(new Polyline
        {
            new Point3d(0, 0, 0),
            new Point3d(10, 0, 0)
        });
        using var second = first.DuplicateCurve();

        var input = new[]
        {
            Entry(first, Guid.NewGuid()),
            Entry(second, Guid.NewGuid())
        };

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            input,
            new TerrainValidationOptions(true, false, false, false, 0.001));

        Assert.Single(result.Objects);
        Assert.Equal(1, result.Summary.DuplicateObjectsRemoved);
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void PrepareValidation_Overkill_RemovesSegmentsFromJoinedDuplicateCurve()
    {
        using var source = new PolylineCurve(new Polyline
        {
            new Point3d(0, 0, 0),
            new Point3d(10, 0, 0),
            new Point3d(0, 0, 0),
            new Point3d(10, 0, 0)
        });

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            new[] { Entry(source, Guid.NewGuid()) },
            new TerrainValidationOptions(true, false, false, false, 0.001));

        Assert.Equal(2, result.Summary.DuplicateSegmentsRemoved);
        Assert.True(result.Objects[0].Curve!.TryGetPolyline(out Polyline cleaned));
        Assert.Equal(2, cleaned.Count);
        Assert.True(cleaned[0].EpsilonEquals(new Point3d(0, 0, 0), 1e-6));
        Assert.True(cleaned[1].EpsilonEquals(new Point3d(10, 0, 0), 1e-6));
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void PrepareValidation_Overkill_RemovesRetracedSegmentsFromPolyCurve()
    {
        using var source = new PolyCurve();
        source.AppendSegment(new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0)));
        source.AppendSegment(new LineCurve(new Point3d(10, 0, 0), new Point3d(20, 5, 0)));
        source.AppendSegment(new LineCurve(new Point3d(20, 5, 0), new Point3d(10, 0, 0)));
        source.AppendSegment(new LineCurve(new Point3d(10, 0, 0), new Point3d(5, 0, 0)));

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            new[] { Entry(source, Guid.NewGuid()) },
            new TerrainValidationOptions(true, false, false, false, 0.001));

        Assert.Equal(2, result.Summary.DuplicateSegmentsRemoved);
        Assert.Single(result.Objects);
        Assert.Equal(new Point3d(0, 0, 0), result.Objects[0].Curve!.PointAtStart);
        Assert.Equal(new Point3d(20, 5, 0), result.Objects[0].Curve!.PointAtEnd);
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void PrepareValidation_JoinNearEndpoints_CombinesAdjacentCurves()
    {
        Guid retainedId = Guid.NewGuid();
        using var first = new PolylineCurve(new Polyline
        {
            new Point3d(0, 0, 0),
            new Point3d(10, 0, 0)
        });
        using var second = new PolylineCurve(new Polyline
        {
            new Point3d(10.0005, 0, 0),
            new Point3d(20, 0, 0)
        });

        var input = new[]
        {
            Entry(first, retainedId, layerIndex: 4),
            Entry(second, Guid.NewGuid(), layerIndex: 4)
        };

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            input,
            new TerrainValidationOptions(false, true, false, false, 0.001));

        Assert.Single(result.Objects);
        Assert.Equal(1, result.Summary.JoinedCurveCount);
        Assert.Equal(retainedId, result.Objects[0].ObjectId);
        Assert.Equal(4, result.Objects[0].Attributes.LayerIndex);
        Assert.True(result.Objects[0].GeometryChanged);
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void PrepareValidation_JoinNearEndpoints_DifferentLayersRemainSeparate()
    {
        using var first = new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0));
        using var second = new LineCurve(new Point3d(10, 0, 0), new Point3d(20, 0, 0));

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            new[]
            {
                Entry(first, Guid.NewGuid(), layerIndex: 2),
                Entry(second, Guid.NewGuid(), layerIndex: 3)
            },
            new TerrainValidationOptions(false, true, false, false, 0.001));

        Assert.Equal(2, result.Objects.Count);
        Assert.Equal(0, result.Summary.JoinedCurveCount);
        Assert.Contains(result.Objects, entry => entry.Attributes.LayerIndex == 2);
        Assert.Contains(result.Objects, entry => entry.Attributes.LayerIndex == 3);
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void PrepareValidation_DuplicateObjectsOnDifferentLayersRemainSeparate()
    {
        using var source = new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0));

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            new[]
            {
                Entry(source, Guid.NewGuid(), layerIndex: 2),
                Entry(source, Guid.NewGuid(), layerIndex: 3)
            },
            new TerrainValidationOptions(true, false, false, false, 0.001));

        Assert.Equal(2, result.Objects.Count);
        Assert.Equal(0, result.Summary.DuplicateObjectsRemoved);
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void PrepareValidation_CollapseShortSegments_RemovesInteriorVertex()
    {
        using var source = new PolylineCurve(new Polyline
        {
            new Point3d(0, 0, 0),
            new Point3d(0.25, 0, 0),
            new Point3d(10, 0, 0)
        });

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            new[] { Entry(source, Guid.NewGuid()) },
            new TerrainValidationOptions(false, false, true, false, 0.5));

        Assert.Equal(1, result.Summary.ShortSegmentsCollapsed);
        Assert.True(result.Objects[0].Curve!.TryGetPolyline(out Polyline cleaned));
        Assert.Equal(2, cleaned.Count);
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void PrepareValidation_CollapseShortSegments_RemovesShortClosingSegment()
    {
        using var source = new PolylineCurve(new Polyline
        {
            new Point3d(0, 0, 0),
            new Point3d(10, 0, 0),
            new Point3d(10, 10, 0),
            new Point3d(0, 0.1, 0),
            new Point3d(0, 0, 0)
        });

        TerrainValidationPreparation result = TerrainInputCommandAlgorithms.PrepareValidation(
            new[] { Entry(source, Guid.NewGuid()) },
            new TerrainValidationOptions(false, false, true, false, 0.5));

        Assert.Equal(1, result.Summary.ShortSegmentsCollapsed);
        Assert.True(result.Objects[0].Curve!.TryGetPolyline(out Polyline cleaned));
        Assert.True(cleaned.IsClosed);
        Assert.Equal(4, cleaned.Count);
        Dispose(result.Objects);
    }

    [RhinoNativeFact]
    public void NormalizeSplitParameters_UsesArcLengthInsteadOfParameterUnits()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        curve.Domain = new Interval(0, 1);

        double[] result = TerrainInputCommandAlgorithms.NormalizeSplitParameters(
            curve,
            new[] { 0.5, 0.50001, 0.51 },
            tolerance: 0.01);

        Assert.Equal(new[] { 0.5, 0.51 }, result);
    }

    [RhinoNativeFact]
    public void GetIntersectionSplitParameters_CrossingCurves_ReturnsInteriorParameter()
    {
        using var horizontal = new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0));
        using var vertical = new LineCurve(new Point3d(5, -5, 0), new Point3d(5, 5, 0));

        TerrainCurveSplitParameters result = TerrainInputCommandAlgorithms.GetIntersectionSplitParameters(
            horizontal,
            vertical,
            0.001);

        Assert.True(result.HasIntersection);
        Assert.Single(result.Parameters);
        Assert.Equal(0.5, result.Parameters[0], 6);
    }

    [RhinoNativeFact]
    public void TryCreateDrapedPolyline_SamplesVerticalTerrainHits()
    {
        using var mesh = new Mesh();
        int a = mesh.Vertices.Add(0, 0, 10);
        int b = mesh.Vertices.Add(10, 0, 10);
        int c = mesh.Vertices.Add(10, 10, 20);
        int d = mesh.Vertices.Add(0, 10, 20);
        mesh.Faces.AddFace(a, b, c, d);
        mesh.Normals.ComputeNormals();

        using var source = new LineCurve(new Point3d(1, 5, 0), new Point3d(9, 5, 0));
        bool succeeded = TerrainInputCommandAlgorithms.TryCreateDrapedPolyline(
            source,
            new[] { mesh },
            2.0,
            0.001,
            out Polyline result,
            out string? error);

        Assert.True(succeeded, error);
        Assert.Equal(5, result.Count);
        Assert.Equal(15.0, result[0].Z, 6);
        Assert.Equal(15.0, result[^1].Z, 6);
    }

    [RhinoNativeFact]
    public void TryCreateDrapedPolyline_DegreeThreeCurve_PreservesPlanEndpoints()
    {
        using var mesh = new Mesh();
        int a = mesh.Vertices.Add(0, 0, 5);
        int b = mesh.Vertices.Add(10, 0, 5);
        int c = mesh.Vertices.Add(10, 10, 5);
        int d = mesh.Vertices.Add(0, 10, 5);
        mesh.Faces.AddFace(a, b, c, d);

        using var source = NurbsCurve.Create(
            periodic: false,
            degree: 3,
            new[]
            {
                new Point3d(1, 5, 100),
                new Point3d(3, 5, 100),
                new Point3d(7, 5, 100),
                new Point3d(9, 5, 100)
            });

        bool succeeded = TerrainInputCommandAlgorithms.TryCreateDrapedPolyline(
            source,
            new[] { mesh },
            2.0,
            0.001,
            out Polyline result,
            out string? error);

        Assert.True(succeeded, error);
        Assert.True(result[0].EpsilonEquals(new Point3d(1, 5, 5), 1e-6));
        Assert.True(result[^1].EpsilonEquals(new Point3d(9, 5, 5), 1e-6));
    }

    [RhinoNativeFact]
    public void TryCreateWallRails_AppliesPlanAndSignedHeightOffsets()
    {
        bool succeeded = TerrainInputCommandAlgorithms.TryCreateWallRails(
            new[]
            {
                new Point3d(0, 0, 5),
                new Point3d(10, 0, 5)
            },
            2.0,
            -3.0,
            0.001,
            out Polyline source,
            out Polyline generated,
            out string? error);

        Assert.True(succeeded, error);
        Assert.Equal(5.0, source[0].Z, 6);
        Assert.Equal(2.0, Math.Abs(generated[0].Y), 6);
        Assert.Equal(2.0, Math.Abs(generated[1].Y), 6);
        Assert.Equal(2.0, generated[0].Z, 6);
        Assert.Equal(2.0, generated[1].Z, 6);
    }

    private static TerrainInputGeometry Entry(Curve curve, Guid id, int layerIndex = 0)
    {
        return new TerrainInputGeometry
        {
            ObjectId = id,
            Curve = curve.DuplicateCurve(),
            Attributes = new ObjectAttributes { LayerIndex = layerIndex }
        };
    }

    private static void Dispose(IEnumerable<TerrainInputGeometry> entries)
    {
        foreach (TerrainInputGeometry entry in entries)
            entry.Curve?.Dispose();
    }
}
