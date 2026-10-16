using MoleHill.Core.Processing;
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
        Assert.Equal(horizontal.Domain.ParameterAt(0.5), result.Parameters[0], 6); // a LineCurve's domain is its length
    }

    [RhinoNativeFact]
    public void DrapeCurve_SamplesVerticalTerrainHits()
    {
        using var mesh = new Mesh();
        int a = mesh.Vertices.Add(0, 0, 10);
        int b = mesh.Vertices.Add(10, 0, 10);
        int c = mesh.Vertices.Add(10, 10, 20);
        int d = mesh.Vertices.Add(0, 10, 20);
        mesh.Faces.AddFace(a, b, c, d);
        mesh.Normals.ComputeNormals();

        using var source = new LineCurve(new Point3d(1, 5, 0), new Point3d(9, 5, 0));
        CurveDrapeResult drape = Drape(source, mesh, 2.0);

        Assert.True(drape.Succeeded, drape.Failure);
        double[] run = Assert.Single(drape.Runs);
        Assert.Equal(5, run.Length / 3);
        Assert.Equal(15.0, run[2], 6);
        Assert.Equal(15.0, run[^1], 6);
    }

    [RhinoNativeFact]
    public void DrapeCurve_DegreeThreeCurve_PreservesPlanEndpoints()
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

        CurveDrapeResult drape = Drape(source, mesh, 2.0);

        Assert.True(drape.Succeeded, drape.Failure);
        double[] run = Assert.Single(drape.Runs);
        Assert.True(new Point3d(run[0], run[1], run[2]).EpsilonEquals(new Point3d(1, 5, 5), 1e-6));
        Assert.True(new Point3d(run[^3], run[^2], run[^1]).EpsilonEquals(new Point3d(9, 5, 5), 1e-6));
    }

    [RhinoNativeFact]
    public void DrapeCurve_PolylineCorner_IsKeptAsAStation()
    {
        using var mesh = new Mesh();
        int a = mesh.Vertices.Add(0, 0, 5);
        int b = mesh.Vertices.Add(10, 0, 5);
        int c = mesh.Vertices.Add(10, 10, 5);
        int d = mesh.Vertices.Add(0, 10, 5);
        mesh.Faces.AddFace(a, b, c, d);
        using var source = new PolylineCurve(new[] { new Point3d(1, 1, 0), new Point3d(4.3, 1, 0), new Point3d(4.3, 8, 0) });

        CurveDrapeResult drape = Drape(source, mesh, 3.0);

        double[] run = Assert.Single(drape.Runs);
        Assert.Contains(Enumerable.Range(0, run.Length / 3),
            i => Math.Abs(run[i * 3] - 4.3) < 1e-6 && Math.Abs(run[i * 3 + 1] - 1.0) < 1e-6);
    }

    [RhinoNativeFact]
    public void TryBuildDrapedSection_TiesTheSectionBackIntoTheCurve()
    {
        using var mesh = new Mesh();
        int a = mesh.Vertices.Add(0, -5, 5);
        int b = mesh.Vertices.Add(10, -5, 5);
        int c = mesh.Vertices.Add(10, 5, 5);
        int d = mesh.Vertices.Add(0, 5, 5);
        mesh.Faces.AddFace(a, b, c, d);
        using var source = new LineCurve(new Point3d(0, 0, 100), new Point3d(10, 0, 100));
        DrapeTargets targets = DrapeTargets.Create(new[] { mesh }, 0.001, out _);

        using Curve? result = TerrainInputCommandAlgorithms.TryBuildDrapedSection(
            source, 3.0, 7.0, targets, lowest: false,
            new CurveDrapeOptions { Spacing = 0.5, Tolerance = 0.001, MissPolicy = DrapeMissPolicy.KeepOriginal, TieInLength = 1.0 },
            out CurveDrapeResult? drape, out string? error);

        Assert.NotNull(result);
        Assert.Null(error);
        Assert.True(result!.PointAtStart.EpsilonEquals(new Point3d(0, 0, 100), 1e-6));
        Assert.True(result.PointAtEnd.EpsilonEquals(new Point3d(10, 0, 100), 1e-6));
        Assert.True(result.ClosestPoint(new Point3d(5, 0, 5), out double t));
        Assert.Equal(5.0, result.PointAt(t).Z, 6);
    }

    [RhinoNativeFact]
    public void DrapeCurveLoose_Polyline_MovesOnlyItsVertices()
    {
        using Mesh mesh = SlopedPlane();
        using var source = new PolylineCurve(new[] { new Point3d(1, 1, 50), new Point3d(5, 2, 50), new Point3d(9, 8, 50) });
        DrapeTargets targets = DrapeTargets.Create(new[] { mesh }, 0.001, out _);

        using Curve? result = TerrainInputCommandAlgorithms.DrapeCurveLoose(
            source, targets, lowestHit: false, DrapeMissPolicy.KeepOriginal, out LooseDrapeResult report);

        Assert.True(report.Succeeded, report.Failure);
        Assert.True(result!.TryGetPolyline(out Polyline polyline));
        Assert.Equal(3, polyline.Count);
        Assert.Equal(0.5, polyline[1].Z, 6);
        Assert.True(polyline[2].EpsilonEquals(new Point3d(9, 8, 0.9), 1e-6));
    }

    [RhinoNativeFact]
    public void DrapeCurveLoose_Nurbs_KeepsDegreeAndPointCount()
    {
        using Mesh mesh = SlopedPlane();
        using var source = NurbsCurve.Create(false, 3, new[]
        {
            new Point3d(1, 5, 50), new Point3d(3, 6, 50), new Point3d(5, 4, 50), new Point3d(7, 6, 50), new Point3d(9, 5, 50)
        });

        using Curve? result = TerrainInputCommandAlgorithms.DrapeCurveLoose(
            source, DrapeTargets.Create(new[] { mesh }, 0.001, out _), false, DrapeMissPolicy.KeepOriginal, out _);

        var nurbs = Assert.IsType<NurbsCurve>(result);
        Assert.Equal(3, nurbs.Degree);
        Assert.Equal(5, nurbs.Points.Count);
        foreach (double t in nurbs.GrevilleParameters())
        {
            Point3d point = nurbs.PointAt(t);
            Assert.Equal(point.X / 10.0, point.Z, 6);
        }
    }

    private static Mesh SlopedPlane()
    {
        var mesh = new Mesh();
        int a = mesh.Vertices.Add(0, 0, 0);
        int b = mesh.Vertices.Add(10, 0, 1);
        int c = mesh.Vertices.Add(10, 10, 1);
        int d = mesh.Vertices.Add(0, 10, 0);
        mesh.Faces.AddFace(a, b, c, d);
        return mesh;
    }

    private static CurveDrapeResult Drape(Curve source, Mesh mesh, double spacing)
    {
        DrapeTargets targets = DrapeTargets.Create(new[] { mesh }, 0.001, out _);
        return TerrainInputCommandAlgorithms.DrapeCurve(source, targets, lowestHit: false, new CurveDrapeOptions
        {
            Spacing = spacing,
            Tolerance = 0.001
        });
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
