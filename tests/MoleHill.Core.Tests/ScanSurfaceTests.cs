using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ScanSurfaceTests
{
    private static (ScanGround.Result Ground, int[] Colours) Scene(double width, double cell,
        Func<double, double, double> height, Func<double, double, int> colour)
    {
        int size = (int)Math.Round(width / cell) + 1;
        var values = new double[size * size];
        var colours = new int[values.Length];
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                values[row * size + column] = height(column * cell, row * cell);
                colours[row * size + column] = colour(column * cell, row * cell);
            }
        var grid = new ScanGrid(0, 0, cell, size, size, values);
        bool[] known = Enumerable.Repeat(true, values.Length).ToArray();
        return (new ScanGround.Result
        {
            Surface = grid,
            Ground = grid,
            IsGround = known,
            Domain = (bool[])known.Clone(),
            DomainArea = values.Length * cell * cell,
            FilledArea = 0,
            ObjectCount = 0,
            StepCount = 0,
        }, colours);
    }

    private static ScanSurfaceAreas.Options AreaOptions => new()
    {
        MaximumPavedRoughness = 0.1,
        MinimumArea = 0.1,
        PlanTolerance = 0.1,
        VerticalTolerance = 0.02,
    };

    private static ScanRoadEdges.Options RoadOptions => new()
    {
        MinimumKerbHeight = 0.06,
        MaximumKerbHeight = 0.3,
        ProfileReach = 0.75,
        MaximumKerbWidth = 0.5,
        MinimumLength = 2,
        PlanTolerance = 0.1,
        VerticalTolerance = 0.015,
    };

    [Fact]
    public void Fit_OpenBoundaryAtSparseEdge_HoldsBothJunctionSamples()
    {
        var values = new double[25]; values[0] = double.NaN;
        var grid = new ScanGrid(0, 0, 1, 5, 5, values);
        var raw = new double[] { 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 0, 0 };
        var curve = ScanBoundaryFitter.Fit(raw, false, grid, null, .5, .05);
        Assert.Equal(raw.Take(3), curve.PointsXyz.Take(3));
        Assert.Equal(raw.TakeLast(3), curve.PointsXyz.TakeLast(3));
    }

    [Fact]
    public void Classify_ThreeClassesMeetingAtAnIslandCorner_KeepsSharedJunctionsConnected()
    {
        var scene = Scene(10, .5, (x, y) => .1 * y,
            (x, y) => x >= 4 && x <= 6 && y >= 4 && y <= 6 ? 0x808080 :
                x > 6 && y > 6 ? 0xCC1010 : 0x328432);
        var areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        Assert.NotEmpty(areas.Boundaries);
        Assert.All(areas.Boundaries, b => Assert.Equal(b.Points.Take(3), b.Points.TakeLast(3)));
    }

    [Fact]
    public void Rasterize_TextureUvAndOverlappingFaces_UsesTheHighestTriangleColour()
    {
        var mesh = IndexedTriMesh.FromArrays(
            new double[] { 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 0, 2, 0, 2, 2 },
            new[] { 0, 1, 2, 3, 4, 5 });
        var source = new ScanColourSource(new[] { 0x00FF00, 0x202020 });
        ScanRasterizer.Rasters raster = ScanRasterizer.Rasterize(mesh, 0.5, colours: source);
        Assert.Equal(0x202020, raster.Colours![0]);
        Assert.Equal(-1, raster.Colours[4 * raster.Top.Columns + 4]);
        var texture = new ScanTexture(2, 2, new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 });
        Assert.Equal(0xFF0000, texture.Sample(0, 1));
        Assert.Equal(0x0000FF, texture.Sample(0, 0));
        Assert.Equal(0x00FF00, texture.Sample(1, 1));
        var uv = new ScanColourSource(texture, new float[] { 0, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 1 });
        raster = ScanRasterizer.Rasterize(mesh, 0.5, colours: uv);
        Assert.Equal(0x0000FF, raster.Colours![0]);
    }

    [Fact]
    public void Rasterize_FaceTexturedFromAStretchedTexel_IsMarkedFilled()
    {
        // Eight 1 m cells of measured surface, each textured over a 64-texel patch, and one 4 m cell beside
        // them, a reconstruction's gap fill: its corners map to one texel's worth of the bitmap.
        var vertices = new List<double>(); var faces = new List<int>(); var uv = new List<float>();
        void Square(double x, double size, float u, float span)
        {
            int start = vertices.Count / 3;
            vertices.AddRange(new[] { x, 0, 0, x + size, 0, 0, x + size, size, 0, x, size, 0 });
            faces.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            uv.AddRange(new[] { u, 0, u + span, 0, u + span, span, u, 0, u + span, span, u, span });
        }
        for (int i = 0; i < 8; i++) Square(i, 1, i / 8f, 1 / 8f);
        Square(8, 4, .5f, 1 / 64f);
        var texture = new ScanTexture(64, 64, new byte[64 * 64 * 3]);
        var mesh = IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());
        ScanRasterizer.Rasters raster = ScanRasterizer.Rasterize(mesh, .25, colours: new ScanColourSource(texture, uv.ToArray()));
        ScanGrid top = raster.Top;
        bool Filled(double x, double y) => raster.Filled![(int)Math.Round(y / top.CellSize) * top.Columns + (int)Math.Round(x / top.CellSize)];
        Assert.False(Filled(3.5, .5));
        Assert.True(Filled(10, 2));
        // Per-face colours carry no texture density: unknown, not measured.
        Assert.Null(ScanRasterizer.Rasterize(mesh, .25, colours: new ScanColourSource(new int[faces.Count / 3])).Filled);
    }

    [Fact]
    public void Classify_DarkRoadAndShadedGrass_UsesChromaticityAndExcludesRemovedRoofs()
    {
        var scene = Scene(10, 0.5, (x, y) => 0.1 * y,
            (x, y) => x < 5 ? (y < 5 ? 0x808080 : 0x0C0E0E) : (y < 5 ? 0x328432 : 0x0C1E0E));
        scene.Ground.IsGround[3 * scene.Ground.Ground.Columns + 3] = false;
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        int n = areas.Ground.Columns;
        Assert.Equal(ScanSurfaceKind.Paved, areas.Classes[2 * n + 2]);
        Assert.Equal(ScanSurfaceKind.Paved, areas.Classes[15 * n + 2]);
        Assert.Equal(ScanSurfaceKind.Soft, areas.Classes[2 * n + 15]);
        Assert.Equal(ScanSurfaceKind.Soft, areas.Classes[15 * n + 15]);
        Assert.Equal(ScanSurfaceKind.Unknown, areas.Classes[3 * n + 3]);
        Assert.True(areas.PavedArea > 40 && areas.SoftArea > 40);
        Assert.All(areas.Boundaries, boundary => Assert.Equal(boundary.Points.Take(3), boundary.Points.TakeLast(3)));
    }

    [Fact]
    public void Classify_FootpathNarrowerThanTheEvidenceRadius_PavesThePathAndLeavesSteppingStonesAsLawn()
    {
        // A half-metre path across the lawn diagonally; 0.4 m stones a metre apart in the lower corner.
        bool Path(double x, double y) => Math.Abs(x - y) <= 0.25 * Math.Sqrt(2);
        bool Stone(double x, double y) => x > 14 && y < 6 && x % 1 < 0.4 && y % 1 < 0.4;
        var scene = Scene(20, 0.05, (x, y) => 0, (x, y) => Path(x, y) || Stone(x, y) ? 0x808080 : 0x328432);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, new()
        {
            MaximumPavedRoughness = 0.1,
            MinimumArea = 10,
            PlanTolerance = 0.5,
            VerticalTolerance = 0.02,
            SmoothingRadius = 0.3,
        });
        int n = areas.Ground.Columns;
        for (int k = 20; k <= 380; k += 40)
            Assert.Equal(ScanSurfaceKind.Paved, areas.Classes[k * n + k]);
        Assert.Equal(ScanSurfaceKind.Soft, areas.Classes[(int)Math.Round(2.2 / 0.05) * n + (int)Math.Round(16.2 / 0.05)]);
        Assert.InRange(areas.PavedArea, 0.5 * 20 * Math.Sqrt(2) * 0.8, 0.5 * 20 * Math.Sqrt(2) * 1.2);
    }

    [Fact]
    public void Classify_IsolatedPathStretchUnderTheMinimumArea_KeepsThePathAndCullsAShortGreyPatch()
    {
        // 4 m² of path, 8 m long, and a 1 m by 3.5 m grey patch: both narrower than the averaging erases, both
        // under the 10 m² minimum, but only the path is long enough to be one.
        bool Path(double x, double y) => x is >= 4 and <= 12 && y is >= 4 and <= 4.5;
        bool Patch(double x, double y) => x is >= 6 and <= 7 && y is >= 10 and <= 13.5;
        var scene = Scene(16, 0.05, (x, y) => 0, (x, y) => Path(x, y) || Patch(x, y) ? 0x808080 : 0x328432);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, new()
        {
            MaximumPavedRoughness = 0.1,
            MinimumArea = 10,
            PlanTolerance = 0.5,
            VerticalTolerance = 0.02,
            SmoothingRadius = 0.3,
        });
        int n = areas.Ground.Columns;
        int Node(double x, double y) => (int)Math.Round(y / 0.05) * n + (int)Math.Round(x / 0.05);
        Assert.Equal(ScanSurfaceKind.Paved, areas.Classes[Node(8, 4.25)]);
        Assert.Equal(ScanSurfaceKind.Soft, areas.Classes[Node(6.5, 11.75)]);
    }

    [Fact]
    public void Clip_SoftAreaWithPavedIsland_RetainsTheHoleAndClipsALongChordExactly()
    {
        var scene = Scene(10, 0.5, (x, y) => 0, (x, y) => x >= 4 && x <= 6 && y >= 4 && y <= 6 ? 0x808080 : 0x328432);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        Assert.Equal(2, areas.Boundaries.Count(boundary => boundary.Kind == ScanSurfaceKind.Soft));
        Assert.Single(areas.Boundaries, boundary => boundary.IsSharedDuplicate);
        Assert.Equal(2, areas.Boundaries.Count(boundary => !boundary.IsSharedDuplicate));
        var region = new ScanSoftRegion(areas);
        Assert.False(region.Contains(5, 5));
        Assert.True(region.Contains(2, 5));
        IReadOnlyList<RegionInputClipper.InputPolyline> clipped = region.Clip(new double[] { -100, 5, 2, 100, 5, 2 }, false);
        Assert.Equal(2, clipped.Count);
        Assert.All(clipped, line => Assert.Equal(2, line.PointCount));
        Assert.Equal(3.75, clipped[0].Points[^3], 8);
        Assert.Equal(6.25, clipped[1].Points[0], 8);
        Assert.All(clipped, line => Assert.All(line.Points.Where((_, i) => i % 3 == 2), z => Assert.Equal(2, z)));
    }

    [Fact]
    public void Classify_SubMinimumAreaHole_ClosesDrawingMaskWithoutInventingObservedGround()
    {
        var scene = Scene(10, 0.1, (x, y) => 0, (x, y) => 0x328432);
        int centre = 50 * scene.Ground.Ground.Columns + 50;
        scene.Ground.IsGround[centre] = false;
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, new()
        {
            MaximumPavedRoughness = 0.1,
            MinimumArea = 2,
            PlanTolerance = 0.1,
            VerticalTolerance = 0.02,
        });
        Assert.Equal(ScanSurfaceKind.Unknown, areas.Classes[centre]);
        Assert.True(areas.SoftMask[centre]);
        Assert.Single(areas.Boundaries);
    }

    [Fact]
    public void Trace_ClearKerbsAlongBothRoadSides_EmitsPairsWithMeasuredEvidence()
    {
        var scene = Scene(20, 0.1, (x, y) => 0.05 * y + (x < 8 || x > 12 ? 0.15 : 0),
            (x, y) => x >= 8 && x <= 12 ? 0x606060 : 0x328432);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        IReadOnlyList<ScanRoadEdges.EdgeLine> lines = ScanRoadEdges.Trace(scene.Ground, areas, RoadOptions);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.True(line.IsKerb);
            Assert.NotNull(line.TopCurve);
            Assert.InRange(line.MeanStep, 0.149, 0.151);
            Assert.InRange(line.MeasuredShare, 0.99, 1);
            Assert.True(line.BottomCurve.ControlPointCount < 10);
        });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.12)]
    public void Trace_ColourEdgeOnPlainOrSlopingGround_EmitsSingleRoadEdges(double slope)
    {
        var scene = Scene(20, 0.1, (x, y) => slope * x + 0.05 * y,
            (x, y) => x >= 8 && x <= 12 ? 0x606060 : 0x328432);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        IReadOnlyList<ScanRoadEdges.EdgeLine> lines = ScanRoadEdges.Trace(scene.Ground, areas, RoadOptions);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => { Assert.False(line.IsKerb); Assert.Null(line.Top); });
    }

    [Fact]
    public void Trace_ShortKerbEvidence_DiscardsRunsBelowMinimumLength()
    {
        var scene = Scene(20, .1, (x, y) => x < 8 && y > 4.5 && y < 5.5 ? .15 : 0,
            (x, y) => x >= 8 && x <= 12 ? 0x606060 : 0x328432);
        var areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        var roads = ScanRoadEdges.Trace(scene.Ground, areas, RoadOptions);
        Assert.NotEmpty(roads);
        Assert.All(roads, road =>
        {
            double length = 0;
            var points = road.BottomCurve.PointsXyz;
            for (int i = 3; i < points.Length; i += 3)
                length += Math.Sqrt(Math.Pow(points[i] - points[i - 3], 2) + Math.Pow(points[i + 1] - points[i - 2], 2));
            Assert.True(length >= RoadOptions.MinimumLength, $"Short road run: {length}");
        });
    }

    [Fact]
    public void Trace_KerbDisappearsAlongRoad_SwitchesToSingleBreaklines()
    {
        var scene = Scene(20, 0.1, (x, y) => (x < 8 || x > 12) && y < 10 ? 0.15 : 0,
            (x, y) => x >= 8 && x <= 12 ? 0x606060 : 0x328432);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        IReadOnlyList<ScanRoadEdges.EdgeLine> lines = ScanRoadEdges.Trace(scene.Ground, areas, RoadOptions);
        Assert.Contains(lines, line => line.IsKerb);
        Assert.Contains(lines, line => !line.IsKerb);
        Assert.All(lines.Where(line => line.IsKerb), line => Assert.InRange(line.MeasuredShare, 0.65, 1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    public void Trace_NoisyStraightRoadWithRollingProfile_AddsHeightControlsOnTheSamePlanLine(double scale)
    {
        double Left(double y) => 8 + .15 * y + .1 * Math.Sin(y * 7);
        var scene = Scene(20 * scale, .1 * scale, (x, y) => scale * (.02 * y / scale + .15 * Math.Sin(y / scale * .8)),
            (x, y) => x / scale >= Left(y / scale) && x / scale <= Left(y / scale) + 4 ? 0x606060 : 0x328432);
        var areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, new()
        {
            MaximumPavedRoughness = .1 * scale,
            MinimumArea = 2 * scale * scale,
            PlanTolerance = .5 * scale,
            VerticalTolerance = .05 * scale,
        });
        var roads = ScanRoadEdges.Trace(scene.Ground, areas, new()
        {
            MinimumKerbHeight = .06 * scale,
            MaximumKerbHeight = .3 * scale,
            ProfileReach = scale,
            MaximumKerbWidth = .6 * scale,
            MinimumLength = 2 * scale,
            PlanTolerance = .5 * scale,
            VerticalTolerance = .015 * scale,
            RoadVerticalTolerance = .05 * scale,
        });
        Assert.Equal(2, roads.Count);
        foreach (var road in roads)
        {
            var piece = Assert.Single(road.BottomCurve.Pieces);
            Assert.InRange(piece.ControlPointCount, 5, 40);
            var a = piece.Start; var b = piece.End;
            double length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            for (int i = 0; i < piece.ControlPoints.Length; i += 3)
                Assert.InRange(Math.Abs(MoleHill.Core.Geometry.Geometry2D.Orient(a.X, a.Y, b.X, b.Y,
                    piece.ControlPoints[i], piece.ControlPoints[i + 1])) / length, 0, scale * 1e-9);
            for (int i = 0; i <= 100; i++)
            {
                var p = piece.PointAt(i / 100.0);
                double y = p.Y / scale;
                Assert.InRange(Math.Abs(p.Z / scale - (.02 * y + .15 * Math.Sin(y * .8))), 0, .055);
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    public void Trace_DiagonalCurvingRoadOnSlope_FitsAtPhysicalScaleWithoutGridCorners(double scale)
    {
        double Left(double y) => 10 + .2 * y + .002 * (y - 20) * (y - 20);
        var scene = Scene(40 * scale, .25 * scale, (x, y) => .03 * x + .1 * y,
            (x, y) => x / scale >= Left(y / scale) && x / scale <= Left(y / scale) + 6 ? 0x606060 : 0x328432);
        var areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, new()
        {
            MaximumPavedRoughness = .1 * scale,
            MinimumArea = 2 * scale * scale,
            PlanTolerance = .5 * scale,
            VerticalTolerance = .015 * scale,
        });
        var roads = ScanRoadEdges.Trace(scene.Ground, areas, new()
        {
            MinimumKerbHeight = .06 * scale,
            MaximumKerbHeight = .3 * scale,
            ProfileReach = scale,
            MaximumKerbWidth = .6 * scale,
            MinimumLength = 2 * scale,
            PlanTolerance = .5 * scale,
            VerticalTolerance = .015 * scale,
        });
        Assert.Equal(2, roads.Count);
        Assert.All(roads, road =>
        {
            Assert.False(road.IsKerb);
            Assert.InRange(road.BottomCurve.ControlPointCount, 3, 28);
            Assert.InRange(road.BottomCurve.Pieces.Count, 1, 6);
            for (int i = 0; i < road.BottomCurve.PointsXyz.Length; i += 3)
            {
                double x = road.BottomCurve.PointsXyz[i] / scale, y = road.BottomCurve.PointsXyz[i + 1] / scale;
                double distance = Math.Min(Math.Abs(x - Left(y)), Math.Abs(x - Left(y) - 6));
                Assert.InRange(distance, 0, .65);
                Assert.InRange(Math.Abs(road.BottomCurve.PointsXyz[i + 2] / scale - (.03 * x + .1 * y)), 0, .015);
            }
        });
        Assert.All(areas.Boundaries.Where(b => b.Kind == ScanSurfaceKind.Paved),
            b => Assert.InRange(b.Curve.ControlPointCount, 4, 45));
        var region = new ScanSoftRegion(areas);
        // Clipping follows the fitted outline rather than stopping at an obsolete raster stair.
        for (double y = 3.31 * scale; y < 38 * scale; y += 3.37 * scale)
        {
            var clipped = region.Clip(new[] { 0.0, y, 0, 40 * scale, y, 0 }, false);
            Assert.Equal(2, clipped.Count);
            double x = clipped[0].Points[^3];
            Assert.True(region.Contains(x - .001 * scale, y));
            Assert.False(region.Contains(x + .001 * scale, y));
            Assert.InRange(Math.Abs(x / scale - Left(y / scale)), 0, .65);
        }
    }

    [Fact]
    public void Recover_RegionWithRoadAcrossMound_OutputsOnlySoftAreaContoursAndSpots()
    {
        var scene = Scene(20, 0.5, (x, y) => 4 * Math.Exp(-((x - 10) * (x - 10) + (y - 10) * (y - 10)) / 35),
            (x, y) => x >= 9 && x <= 11 ? 0x606060 : 0x328432);
        IndexedTriMesh mesh = ScanGroundMesher.Mesh(scene.Ground.Ground, 0.5);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        var region = new ScanSoftRegion(areas);
        double[][] boundaries = areas.Boundaries.Where(boundary => boundary.Kind == ScanSurfaceKind.Paved).Select(boundary => boundary.Points).ToArray();
        IndexedTriMesh? reference = TerrainCurveRecovery.ConstrainReference(mesh, boundaries, 0.1, out string? error);
        Assert.True(reference.HasValue, error);
        TerrainCurveRecovery.Result curves = TerrainCurveRecovery.Recover(reference.Value, new()
        {
            VerticalTolerance = 0.25,
            PlanTolerance = 0.1,
            MaximumCurves = 100,
            LandformLines = false,
            FixedBreaklines = boundaries,
            Region = region,
            FitContours = true,
        });
        Assert.NotEmpty(curves.Contours);
        Assert.Contains(curves.Contours, contour => contour.Curve != null);
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, curves.Termination);
        foreach (TerrainCurveRecovery.RecoveredContour contour in curves.Contours)
        {
            int count = contour.PointsXyz.Length / 3;
            for (int i = 0; i < (contour.IsClosed ? count : count - 1); i++)
            {
                int a = i * 3, b = (i + 1) % count * 3;
                Assert.True(region.Contains((contour.PointsXyz[a] + contour.PointsXyz[b]) * 0.5,
                    (contour.PointsXyz[a + 1] + contour.PointsXyz[b + 1]) * 0.5));
            }
        }
        for (int i = 0; i < curves.SpotPoints.Length; i += 3)
            Assert.True(region.Contains(curves.SpotPoints[i], curves.SpotPoints[i + 1]));
    }

    [Fact]
    public void Recover_AllPavedRegion_ReportsNoSoftVerticesInsteadOfClaimingFullSiteAccuracy()
    {
        var scene = Scene(10, 0.5, (x, y) => 0.1 * x, (x, y) => 0x606060);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(ScanGroundMesher.Mesh(scene.Ground.Ground, 1), new()
        {
            VerticalTolerance = 0.1,
            Region = new ScanSoftRegion(areas),
            FitContours = true,
        });
        Assert.Equal(0, result.EvaluatedVertexCount);
        Assert.Contains("No terrain vertices", result.Diagnostic);
        Assert.Empty(result.Contours);
        Assert.Empty(result.SpotPoints);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    public void TraceGeometry_NoColour_EmitsOnlyKerbSizedMeasuredSteps(double scale)
    {
        var scene = Scene(20 * scale, 0.25 * scale,
            (x, y) => 0.02 * y + (x < 8 * scale ? 0 : 0.15 * scale) + (x < 14 * scale ? 0 : 1.5 * scale),
            (x, y) => 0);
        IndexedTriMesh reference = ScanGroundMesher.Mesh(scene.Ground.Ground, 0.5 * scale);
        IReadOnlyList<ScanRoadEdges.EdgeLine> roads = ScanRoadEdges.TraceGeometry(scene.Ground, new()
        {
            MinimumKerbHeight = 0.06 * scale,
            MaximumKerbHeight = 0.3 * scale,
            ProfileReach = scale,
            MaximumKerbWidth = 0.6 * scale,
            MinimumLength = 2 * scale,
            PlanTolerance = 0.1 * scale,
            VerticalTolerance = 0.015 * scale,
        }, reference);
        Assert.Single(roads);
        Assert.True(roads[0].IsKerb);
        Assert.InRange(roads[0].MeanStep / scale, 0.149, 0.151);
        // The same heights under a removed object are inferred, not measured kerb evidence.
        for (int i = 0; i < scene.Ground.IsGround.Length; i++)
            if (Math.Abs(scene.Ground.Ground.NodeX(i % scene.Ground.Ground.Columns) - 8 * scale) < 2 * scale)
                scene.Ground.IsGround[i] = false;
        Assert.Empty(ScanRoadEdges.TraceGeometry(scene.Ground, new()
        {
            MinimumKerbHeight = 0.06 * scale,
            MaximumKerbHeight = 0.3 * scale,
            ProfileReach = scale,
            MaximumKerbWidth = 0.6 * scale,
            MinimumLength = 2 * scale,
            PlanTolerance = 0.1 * scale,
            VerticalTolerance = 0.015 * scale,
        }, reference));
    }

    [Fact]
    public void Recover_DisconnectedLawns_KeepsContoursNearTheSelectedError()
    {
        var scene = Scene(20, 0.5, (x, y) => 3 * Math.Exp(-((x - 10) * (x - 10) + (y - 10) * (y - 10)) / 35),
            (x, y) => (int)(x / 2) % 2 == 0 ? 0x328432 : 0x606060);
        ScanSurfaceAreas.Result areas = ScanSurfaceAreas.Classify(scene.Ground, scene.Colours, AreaOptions);
        var region = new ScanSoftRegion(areas);
        IndexedTriMesh mesh = ScanGroundMesher.Mesh(scene.Ground.Ground, 0.5);
        TerrainCurveRecovery.Result recovery = TerrainCurveRecovery.Recover(mesh, new()
        {
            VerticalTolerance = 0.25,
            PlanTolerance = 0.1,
            MaximumCurves = 50,
            Region = region,
            FitContours = true,
            LandformLines = false,
        });
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, recovery.Termination);
        Assert.InRange(recovery.Contours.Count, 1, 49);
        Assert.All(recovery.Contours, line => Assert.InRange(
            line.PointsXyz.Where((_, i) => i % 3 == 0).Max() - line.PointsXyz.Where((_, i) => i % 3 == 0).Min(), 0, 2.01));
    }
}
