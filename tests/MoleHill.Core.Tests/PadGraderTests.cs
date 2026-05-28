using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PadGraderTests
{
    [Fact]
    public void Grade_InvalidTerrainVertexArray_ReturnsFailure()
    {
        var pad = new PadGrader.PadBoundary(
            new[] { 1.0, 1.0, 3.0, 1.0, 3.0, 3.0, 1.0, 3.0 },
            4,
            targetZ: 1.0);

        GradingResult? result = PadGrader.Grade(
            new[] { 0.0, 0.0, 0.0 },
            4,
            BuildGridFaces(3),
            8,
            new[] { pad },
            null,
            out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("vertex array", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryTriangulateTopology_InvalidTerrainFace_ReturnsFailure()
    {
        var pad = new PadGrader.PadBoundary(
            new[] { 1.0, 1.0, 3.0, 1.0, 3.0, 3.0, 1.0, 3.0 },
            4,
            targetZ: 1.0);

        bool success = PadGrader.TryTriangulateTopology(
            BuildGridVertices(5, 1.0),
            25,
            new[] { 0, 1, 99 },
            1,
            new[] { pad },
            null,
            0.0,
            0.0,
            out double[] topologyVertices,
            out int topologyVertexCount,
            out int[] topologyFaces,
            out int topologyFaceCount,
            out string? warning);

        Assert.False(success);
        Assert.Empty(topologyVertices);
        Assert.Equal(0, topologyVertexCount);
        Assert.Empty(topologyFaces);
        Assert.Equal(0, topologyFaceCount);
        Assert.Contains("outside the terrain vertex range", warning ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateConstraints_InvalidTerrainFace_ReturnsStructuredWarning()
    {
        var pad = new PadGrader.PadBoundary(
            new[] { 1.0, 1.0, 3.0, 1.0, 3.0, 3.0, 1.0, 3.0 },
            4,
            targetZ: 1.0);

        PadGrader.ConstraintSet constraintSet = PadGrader.CreateConstraints(
            BuildGridVertices(5, 1.0),
            25,
            new[] { 0, 1, 99 },
            1,
            new[] { pad },
            null);

        Assert.Empty(constraintSet.Constraints);
        Assert.Contains("outside the terrain vertex range", constraintSet.Diagnostics.Single(), StringComparison.OrdinalIgnoreCase);
        GradingDiagnostic diagnostic = Assert.Single(constraintSet.StructuredDiagnostics);
        Assert.Equal("grade_pad.input.invalid_terrain", diagnostic.Code);
        Assert.Equal(GradingDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void TriangulatePadTopology_ReturnsStableTopology_ForSameXyInputs()
    {
        var vertices = BuildGridVertices(5, 0.75);
        var faces = BuildGridFaces(5);
        var pads = BuildPads();

        bool first = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVerticesA,
            out var topologyVertexCountA,
            out var topologyFacesA,
            out var topologyFaceCountA,
            out var warningA);

        bool second = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVerticesB,
            out var topologyVertexCountB,
            out var topologyFacesB,
            out var topologyFaceCountB,
            out var warningB);

        Assert.True(first, warningA);
        Assert.True(second, warningB);
        Assert.Equal(topologyVertexCountA, topologyVertexCountB);
        Assert.Equal(topologyFaceCountA, topologyFaceCountB);
        Assert.Equal(topologyVerticesA, topologyVerticesB);
        Assert.Equal(NormalizeFaces(topologyFacesA, topologyFaceCountA), NormalizeFaces(topologyFacesB, topologyFaceCountB));
    }

    [Fact]
    public void ApplyGradingZ_DoesNotChangeVertexOrFaceCount()
    {
        var vertices = BuildGridVertices(5, 0.75);
        var faces = BuildGridFaces(5);
        var pads = BuildPads();

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out var topologyFaces,
            out var topologyFaceCount,
            out var warning);

        Assert.True(success, warning);

        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount, pads);

        Assert.Equal(topologyVertices.Length, gradedVertices.Length);
        Assert.Equal(topologyFaceCount * 3, topologyFaces.Length);
        for (int i = 0; i < topologyVertexCount; i++)
        {
            Assert.Equal(topologyVertices[i * 3], gradedVertices[i * 3], 12);
            Assert.Equal(topologyVertices[i * 3 + 1], gradedVertices[i * 3 + 1], 12);
        }
    }

    [Fact]
    public void ApplyGradingZ_PreservesLaterPadWinsOrdering()
    {
        var pads = BuildPads();
        double[] sampleVertices =
        {
            1.5, 1.5, 0.0,
            0.75, 0.75, 0.0
        };

        double[] gradedVertices = PadGrader.ApplyGradingZ(sampleVertices, sampleVertices.Length / 3, pads);

        Assert.Equal(2.0, gradedVertices[2], 6);
        Assert.Equal(1.0, gradedVertices[5], 6);
    }

    [Fact]
    public void ApplyGradingZ_UsesHigherPadOwnership_WithinSingleModifier()
    {
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    1.125, 1.125,
                    1.875, 1.125,
                    1.875, 1.875,
                    1.125, 1.875
                },
                4,
                2.0),
            new PadGrader.PadBoundary(
                new[]
                {
                    0.375, 0.375,
                    2.625, 0.375,
                    2.625, 2.625,
                    0.375, 2.625
                },
                4,
                1.0)
        };
        double[] sampleVertices =
        {
            1.5, 1.5, 0.0
        };

        double[] gradedVertices = PadGrader.ApplyGradingZ(sampleVertices, sampleVertices.Length / 3, pads);

        Assert.Equal(2.0, gradedVertices[2], 6);
    }

    [Fact]
    public void ApplyGradingZ_EvaluatesPlanarPadSurfaceInsideBoundary()
    {
        double[] vertices = BuildGridVertices(5, 1.0);
        int[] faces = BuildGridFaces(5);
        var pads = new[] { BuildAngledPad() };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out var topologyFaces,
            out var topologyFaceCount,
            out var warning);

        Assert.True(success, warning);

        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount, pads);

        double interiorZ = PadGrader.InterpolateZ(gradedVertices, topologyFaces, topologyFaceCount, 2.0, 2.0);
        double boundaryZ = PadGrader.InterpolateZ(gradedVertices, topologyFaces, topologyFaceCount, 3.0, 2.0);

        Assert.Equal(1.0, interiorZ, 6);
        Assert.Equal(2.0, boundaryZ, 6);
    }

    [Fact]
    public void ApplyGradingZ_UsesBoundaryZForAngledDaylight()
    {
        double[] vertices = BuildGridVertices(5, 1.0);
        int[] faces = BuildGridFaces(5);
        var pads = new[] { BuildAngledPad() };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out var topologyFaces,
            out var topologyFaceCount,
            out var warning);

        Assert.True(success, warning);

        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount, pads);

        int outsideIndex = FindVertexIndex(gradedVertices, topologyVertexCount, 4.0, 2.0);
        Assert.Equal(0.0, gradedVertices[outsideIndex * 3 + 2], 6);
    }

    [Fact]
    public void Grade_ProducesValidGradedMesh_ForIdenticalInputs()
    {
        var vertices = BuildGridVertices(5, 0.75);
        var faces = BuildGridFaces(5);
        var pads = BuildPads();

        var legacy = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out var legacyWarning);

        Assert.True(legacy != null, legacyWarning);
        Assert.True(string.IsNullOrWhiteSpace(legacyWarning) || !legacyWarning.Contains("failed", StringComparison.OrdinalIgnoreCase));

        Assert.True(legacy!.VertexCount > 0);
        Assert.True(legacy.FaceCount > 0);
        Assert.Equal(pads.Length, legacy.OutputPolylines.Count);
        Assert.All(legacy.OutputPolylines, polyline => Assert.True(polyline.IsClosed));
        Assert.All(legacy.Vertices, static value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void Grade_SkipsFallbackBand_WhenBandWidthIsNegligible()
    {
        double[] vertices = BuildGridVertices(9, 1.0);
        int[] faces = BuildGridFaces(9);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    3.0, 3.0,
                    5.0, 3.0,
                    5.0, 5.0,
                    3.0, 5.0
                },
                4,
                2.0,
                slopeAngleDeg: 33.0,
                maxDistance: 0.01)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);
        Assert.True(string.IsNullOrWhiteSpace(warning), warning);
        Assert.Single(result!.PatchSummaries);
        Assert.False(result.PatchSummaries[0].UsesFallbackBand);
    }

    [Fact]
    public void TryTriangulateTopology_InsertsShoulderVertices_WhenOffsetFitsInsideBoundary()
    {
        double[] vertices = BuildGridVertices(9, 1.0);
        int[] faces = BuildGridFaces(9);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    3.0, 3.0,
                    5.0, 3.0,
                    5.0, 5.0,
                    3.0, 5.0
                },
                4,
                2.0,
                slopeAngleDeg: 33.0,
                maxDistance: 1.5)
        };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);
        Assert.True(topologyVertexCount > vertices.Length / 3);
    }

    [Fact]
    public void CreateConstraints_IncludesShoulderRing_WhenOffsetFitsInsideBoundary()
    {
        double[] vertices = BuildGridVertices(9, 1.0);
        int[] faces = BuildGridFaces(9);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    3.0, 3.0,
                    5.0, 3.0,
                    5.0, 5.0,
                    3.0, 5.0
                },
                4,
                2.0,
                slopeAngleDeg: 33.0,
                maxDistance: 1.5)
        };

        PadGrader.ConstraintSet constraintSet = PadGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null);

        Assert.Equal(2, constraintSet.Constraints.Length);
        Assert.Contains(constraintSet.Constraints, constraint => constraint.IsClosed && constraint.PointCount > 4);
        Assert.True(constraintSet.SuggestedEdgeLength > 0.0);
    }

    [Fact]
    public void CreateConstraints_OnCoarseEnvelope_AddsGuideVerticesAcrossShoulderBand()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            100.0, 0.0, 0.0,
            100.0, 100.0, 0.0,
            0.0, 100.0, 0.0
        };
        int[] faces = BuildSquareFaces();
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    40.0, 40.0,
                    60.0, 40.0,
                    60.0, 60.0,
                    40.0, 60.0
                },
                4,
                2.0,
                slopeAngleDeg: 45.0,
                maxDistance: 2.0)
        };

        PadGrader.ConstraintSet constraintSet = PadGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null);

        var remesh = SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraintSet.Constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 1e-3,
                RequestedEdgeLength = constraintSet.SuggestedEdgeLength,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);
        Assert.Contains(
            Enumerable.Range(0, remesh.Vertices.Length / 3),
            index =>
            {
                double x = remesh.Vertices[index * 3];
                double y = remesh.Vertices[index * 3 + 1];
                return x < 40.0 && x > 37.0 && y > 40.0 && y < 60.0;
            });
    }

    [Fact]
    public void CreateConstraints_OnSlopedTerrain_DaylightsAlongTerrainSlope()
    {
        double[] vertices = BuildSlopedGridVertices(11, 10.0, xSlope: 0.5);
        int[] faces = BuildGridFaces(11);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    40.0, 40.0,
                    60.0, 40.0,
                    60.0, 60.0,
                    40.0, 60.0
                },
                4,
                targetZ: 15.0,
                slopeAngleDeg: 45.0)
        };

        PadGrader.ConstraintSet constraintSet = PadGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null);

        double maxShoulderX = constraintSet.Constraints
            .SelectMany(static constraint => Enumerable.Range(0, constraint.PointCount)
                .Select(index => constraint.Points[index * 3]))
            .Max();

        Assert.True(maxShoulderX >= 89.5, $"Expected right daylight near x=90, got max x={maxShoulderX:0.###}.");
    }

    [Fact]
    public void Grade_RotatedPadOnSlopedTerrain_UsesExplicitStitchPatch()
    {
        double[] vertices = BuildSlopedGridVertices(31, 1.0, xSlope: 0.18, ySlope: -0.07);
        int[] faces = BuildGridFaces(31);
        double[] padXy = BuildRotatedRectangle(15.0, 15.0, width: 8.0, height: 4.0, angleDeg: 34.0);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                padXy,
                4,
                targetZ: 4.0,
                slopeAngleDeg: 33.0)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);
        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);
        Assert.True(
            !result.Diagnostics.Any(diagnostic => diagnostic.Contains("using split local patch", StringComparison.OrdinalIgnoreCase)),
            diagnostics);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("using terrain-side stitch loop", StringComparison.OrdinalIgnoreCase) ||
                          diagnostic.Contains("coupled protected patch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("batter slope check", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("topology summary:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.StructuredDiagnostics,
            diagnostic => diagnostic.Code == "grade_pad.slope.check" &&
                          diagnostic.Severity == GradingDiagnosticSeverity.Information &&
                          diagnostic.Operation == "grade_pad" &&
                          diagnostic.TargetIndex == 0);
        Assert.Contains(
            result.StructuredDiagnostics,
            diagnostic => diagnostic.Code == "grade_pad.topology.summary" &&
                          diagnostic.Severity == GradingDiagnosticSeverity.Information &&
                          diagnostic.Operation == "grade_pad");
    }

    [Fact]
    public void Grade_RotatedPadOnDenseTerrain_DoesNotForceRadialSpokeConstraints()
    {
        double[] vertices = BuildSlopedGridVertices(61, 0.5, xSlope: 0.18, ySlope: -0.07);
        int[] faces = BuildGridFaces(61);
        double[] padXy = BuildRotatedRectangle(15.0, 15.0, width: 8.0, height: 4.0, angleDeg: 34.0);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                padXy,
                4,
                targetZ: 4.0,
                slopeAngleDeg: 33.0)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);
        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);
        Assert.True(
            !result.Diagnostics.Any(diagnostic => diagnostic.Contains("using split local patch", StringComparison.OrdinalIgnoreCase)),
            diagnostics);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("patch seam integrity check failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("batter slope check", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grade_RectangularPadOnFlatTerrain_BatterFacesStayNearTargetSlope()
    {
        double[] vertices = BuildGridVertices(41, 1.0);
        int[] faces = BuildGridFaces(41);
        double targetSlopeDeg = 30.0;
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    15.0, 15.0,
                    25.0, 15.0,
                    25.0, 25.0,
                    15.0, 25.0
                },
                4,
                targetZ: 2.0,
                slopeAngleDeg: targetSlopeDeg)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);

        var slopeAngles = CollectBatterTriangleSlopeAngles(
            result!,
            minimumZ: 0.05,
            maximumZ: 1.95,
            includeCentroid: (x, y) =>
                x is > 17.0 and < 23.0 && (y < 15.0 || y > 25.0) ||
                y is > 17.0 and < 23.0 && (x < 15.0 || x > 25.0));
        Assert.True(slopeAngles.Count > 0, "Expected measurable batter triangles.");

        double minSlope = slopeAngles.Min();
        double maxSlope = slopeAngles.Max();
        double maxDelta = slopeAngles.Max(angle => Math.Abs(angle - targetSlopeDeg));
        Assert.True(
            maxDelta <= 4.0,
            $"Expected batter slopes within 4 degrees of {targetSlopeDeg:0.##}; min={minSlope:0.##}, max={maxSlope:0.##}, maxDelta={maxDelta:0.##}.");
    }

    [Fact]
    public void Grade_RectangularPadOnFlatTerrain_CornerFanAvoidsUnderSlopedFacets()
    {
        double[] vertices = BuildGridVertices(41, 1.0);
        int[] faces = BuildGridFaces(41);
        double targetSlopeDeg = 30.0;
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    15.0, 15.0,
                    25.0, 15.0,
                    25.0, 25.0,
                    15.0, 25.0
                },
                4,
                targetZ: 2.0,
                slopeAngleDeg: targetSlopeDeg)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);

        var cornerSlopeAngles = CollectBatterTriangleSlopeAngles(
            result!,
            minimumZ: 0.05,
            maximumZ: 1.95,
            includeCentroid: (x, y) => x < 15.0 && y < 15.0);

        Assert.True(cornerSlopeAngles.Count > 0, "Expected measurable corner batter triangles.");
        double minSlope = cornerSlopeAngles.Min();
        double maxSlope = cornerSlopeAngles.Max();
        Assert.True(
            maxSlope <= targetSlopeDeg + 1.0,
            $"Expected corner fan slopes no steeper than {targetSlopeDeg + 1.0:0.##}; min={minSlope:0.##}, max={maxSlope:0.##}.");
    }

    [Fact]
    public void Grade_ExplicitStripFan_ShoulderSlopesNeverExceedTarget()
    {
        // Corner slopes may be lower than target (diagonal hip is acceptable),
        // but no shoulder triangle may be steeper than the target batter slope.
        double[] vertices = BuildGridVertices(41, 1.0);
        int[] faces = BuildGridFaces(41);
        double targetSlopeDeg = 45.0;
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    15.0, 15.0,
                    25.0, 15.0,
                    25.0, 25.0,
                    15.0, 25.0
                },
                4,
                targetZ: 2.0,
                slopeAngleDeg: targetSlopeDeg)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, $"Grade returned null: {warning}");

        var shoulderSlopeAngles = CollectBatterTriangleSlopeAngles(
            result!,
            minimumZ: 0.05,
            maximumZ: 1.95);

        Assert.True(shoulderSlopeAngles.Count > 0, $"Expected shoulder triangles. Diags={string.Join("|", result!.Diagnostics)}");
        double maxSlope = shoulderSlopeAngles.Max();
        Assert.True(
            maxSlope <= targetSlopeDeg + 1.0,
            $"Expected all shoulder slopes <= {targetSlopeDeg + 1.0:0.##} degrees (steeper is wrong); max={maxSlope:0.##} degrees.");
    }

    [Fact]
    public void Grade_ProtectedApron_KeepsSyntheticShoulderGrading()
    {
        double[] vertices = BuildGridVertices(41, 1.0);
        int[] faces = BuildGridFaces(41);
        double targetSlopeDeg = 30.0;
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    15.0, 15.0,
                    25.0, 15.0,
                    25.0, 25.0,
                    15.0, 25.0
                },
                4,
                targetZ: 2.0,
                slopeAngleDeg: targetSlopeDeg,
                stitchApronDistance: 0.5)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);
        string diagnostics = string.Join("|", result!.Diagnostics);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("protected stitch apron", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("patch did not produce a single closed stitch boundary", diagnostics);
        Assert.DoesNotContain("using split local patch", diagnostics);

        var shoulderSlopeAngles = CollectBatterTriangleSlopeAngles(
            result,
            minimumZ: 0.05,
            maximumZ: 1.95,
            includeCentroid: (x, y) => x is > 17.0 and < 23.0 && y > 25.0);
        Assert.True(shoulderSlopeAngles.Count > 0, $"Expected measurable protected-apron shoulder triangles. Diags={diagnostics}");

        Assert.True(shoulderSlopeAngles.Any(angle => Math.Abs(angle - targetSlopeDeg) <= 4.0),
            $"Expected at least one protected-apron shoulder face near {targetSlopeDeg:0.##} degrees.");
    }

    [Fact]
    public void Grade_MultipleProtectedPads_DoesNotIntroduceInteriorNakedEdges()
    {
        double[] vertices = BuildSlopedGridVertices(81, 1.0, xSlope: 0.04, ySlope: -0.03);
        int[] faces = BuildGridFaces(81);
        var pads = new[]
        {
            new PadGrader.PadBoundary(BuildRotatedRectangle(22.0, 24.0, 10.0, 7.0, 18.0), 4, targetZ: 2.4, slopeAngleDeg: 30.0, stitchApronDistance: 0.5),
            new PadGrader.PadBoundary(BuildRotatedRectangle(43.0, 28.0, 11.0, 7.0, -12.0), 4, targetZ: 2.0, slopeAngleDeg: 30.0, stitchApronDistance: 0.5),
            new PadGrader.PadBoundary(BuildRotatedRectangle(58.0, 48.0, 12.0, 8.0, 24.0), 4, targetZ: 1.6, slopeAngleDeg: 30.0, stitchApronDistance: 0.5),
            new PadGrader.PadBoundary(BuildRegularPolygon(28.0, 56.0, 5.0, 20), 20, targetZ: 2.8, slopeAngleDeg: 30.0, stitchApronDistance: 0.5)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);
        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);
        int interiorNakedEdges = CountInteriorNakedEdges(result, minX: 0.0, maxX: 80.0, minY: 0.0, maxY: 80.0, tolerance: 1e-3);
        Assert.True(interiorNakedEdges == 0, $"Expected no interior naked edges; found {interiorNakedEdges}. Diags={diagnostics}");
    }

    [Fact]
    public void Grade_CoarseRotatedCopiedCase_KeepsCornerFanConstraintsBounded()
    {
        double[] vertices =
        {
            429.2420959472656, -440.863525390625, 26.3985652923584,
            441.60418701171875, -436.677978515625, 26.3985652923584,
            479.488037109375, -439.38629150390625, 26.3985652923584,
            513.3840942382812, -434.70831298828125, 26.3985652923584,
            529.7339477539062, -441.6021728515625, 26.3985652923584,
            559.6422119140625, -440.6173400878906, 26.3985652923584,
            572.403076171875, -439.8786926269531, 26.3985652923584,
            425.9200134277344, -507.8758544921875, 0,
            471.70660400390625, -501.8989562988281, 0,
            519.5863037109375, -501.0912780761719, 0,
            536.3311157226562, -509.16815185546875, 0,
            563.5414428710938, -509.65277099609375, 0,
            587.8737182617188, -506.58355712890625, 0,
            418.71246337890625, -602.7970581054688, 0,
            482.010009765625, -587.3263549804688, 0,
            552.2006225585938, -597.7691040039062, 0,
            598.4663696289062, -581.7898559570312, 0,
            684.7786254882812, -602.0450439453125, 0,
            703.9754028320312, -566.473388671875, 0,
            422.462890625, -382.285888671875, 47.86000061035156,
            464.7332763671875, -371.69891357421875, 47.86000061035156,
            501.8195495605469, -374.4071960449219, 47.86000061035156,
            550.4703369140625, -376.13067626953125, 47.86000061035156,
            566.4214477539062, -374.1610107421875, 47.86000061035156,
            589.9492797851562, -383.2707214355469, 47.86000061035156,
        };
        int[] faces =
        {
            7, 19, 13, 14, 7, 13, 8, 0, 7, 0, 19, 7, 9, 8, 14, 7, 14, 8,
            10, 9, 14, 1, 0, 8, 14, 13, 15, 9, 2, 8, 20, 19, 1, 0, 1, 19,
            2, 20, 1, 21, 2, 3, 9, 3, 2, 22, 21, 3, 20, 2, 21, 2, 1, 8,
            23, 20, 21, 3, 9, 4, 11, 10, 15, 14, 15, 10, 16, 11, 15, 5, 10, 11,
            18, 16, 17, 15, 17, 16, 12, 11, 16, 17, 15, 13, 18, 12, 16, 22, 4, 5,
            5, 4, 10, 23, 22, 5, 3, 4, 22, 6, 12, 18, 11, 6, 5, 6, 24, 23,
            6, 18, 24, 5, 6, 23, 11, 12, 6, 21, 22, 23, 9, 10, 4,
        };
        var pads = new[]
        {
            PadGrader.PadBoundary.CreatePlanar(
                new[]
                {
                    483.88129366474783, -412.07004789207474, 36.41119211856837,
                    528.0569195051114, -439.8191246221211, 36.41119211856837,
                    542.8495618273139, -416.2697201221737, 36.41119211856837,
                    498.6739359869503, -388.5206433921271, 36.41119211856837,
                },
                4,
                planeXCoeff: 0.0,
                planeYCoeff: 0.0,
                planeConstant: 36.41119211856837,
                slopeAngleDeg: 30.0)
        };

        GradingResult? result = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            out string? warning);

        Assert.True(result != null, warning);
        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);
        Assert.True(!diagnostics.Contains("using split local patch", StringComparison.OrdinalIgnoreCase), diagnostics);
        Assert.True(!diagnostics.Contains("stitched merge rejected", StringComparison.OrdinalIgnoreCase), diagnostics);
    }

    [Fact]
    public void TryTriangulateTopology_OnCoarseEnvelope_AddsGuideVerticesAcrossShoulderBand()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            100.0, 0.0, 0.0,
            100.0, 100.0, 0.0,
            0.0, 100.0, 0.0
        };
        int[] faces = BuildSquareFaces();
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    40.0, 40.0,
                    60.0, 40.0,
                    60.0, 60.0,
                    40.0, 60.0
                },
                4,
                2.0,
                slopeAngleDeg: 45.0,
                maxDistance: 2.0)
        };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);
        Assert.True(topologyVertexCount > vertices.Length / 3);
    }

    [Fact]
    public void CreateConstraints_SkipsShoulderRing_WhenOffsetReachesTerrainBoundary()
    {
        double[] vertices = BuildGridVertices(5, 1.0);
        int[] faces = BuildGridFaces(5);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    1.0, 1.0,
                    3.0, 1.0,
                    3.0, 3.0,
                    1.0, 3.0
                },
                4,
                2.0,
                slopeAngleDeg: 33.0,
                maxDistance: 2.0)
        };

        PadGrader.ConstraintSet constraintSet = PadGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null);

        Assert.Equal(2, constraintSet.Constraints.Length);
        Assert.DoesNotContain(
            constraintSet.Diagnostics,
            diagnostic => diagnostic.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(constraintSet.StructuredDiagnostics);
    }

    [Fact]
    public void TryTriangulateTopology_LockCurve_PreservesConstraintIntersections()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            4.0, 0.0, 0.0,
            4.0, 4.0, 0.0,
            0.0, 4.0, 0.0
        };
        int[] faces = BuildSquareFaces();
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    1.0, 1.0,
                    3.0, 1.0,
                    3.0, 3.0,
                    1.0, 3.0
                },
                4,
                1.0)
        };
        var locks = new[]
        {
            new PadGrader.LockCurve(new[] { 0.0, 2.0, 4.0, 2.0 }, 2)
        };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            locks,
            0.0,
            0.0,
            out var topologyVertices,
            out _,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);
        Assert.True(ContainsVertex(topologyVertices, 1.0, 2.0));
        Assert.True(ContainsVertex(topologyVertices, 3.0, 2.0));
    }

    [Fact]
    public void ApplyGradingZ_DoesNotAdjustNearGradeVertexWithinTolerance()
    {
        double[] vertices =
        {
            0.5, 1.5, 0.5005
        };

        var pad = new PadGrader.PadBoundary(
            new[]
            {
                0.0, 0.0,
                1.0, 0.0,
                1.0, 1.0,
                0.0, 1.0
            },
            4,
            targetZ: 1.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        double[] graded = PadGrader.ApplyGradingZ(vertices, 1, new[] { pad });

        Assert.Equal(0.5005, graded[2], 6);
    }

    [Fact]
    public void CreateConstraints_ForConcavePad_BuildsShoulderRing()
    {
        double[] vertices = BuildGridVertices(9, 1.0);
        int[] faces = BuildGridFaces(9);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    2.0, 2.0,
                    6.0, 2.0,
                    6.0, 3.0,
                    3.0, 3.0,
                    3.0, 6.0,
                    2.0, 6.0
                },
                6,
                targetZ: 1.0,
                slopeAngleDeg: 45.0,
                maxDistance: 2.0)
        };

        PadGrader.ConstraintSet constraintSet = PadGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null);

        Assert.True(constraintSet.Constraints.Length >= 2);
        Assert.DoesNotContain(
            constraintSet.Diagnostics,
            diagnostic => diagnostic.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(constraintSet.StructuredDiagnostics);
    }

    private static PadGrader.PadBoundary[] BuildPads()
    {
        return new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    0.375, 0.375,
                    2.625, 0.375,
                    2.625, 2.625,
                    0.375, 2.625
                },
                4,
                1.0),
            new PadGrader.PadBoundary(
                new[]
                {
                    1.125, 1.125,
                    1.875, 1.125,
                    1.875, 1.875,
                    1.125, 1.875
                },
                4,
                2.0)
        };
    }

    private static PadGrader.PadBoundary BuildAngledPad()
    {
        return PadGrader.PadBoundary.CreatePlanar(
            new[]
            {
                1.0, 1.0, 0.0,
                3.0, 1.0, 2.0,
                3.0, 3.0, 2.0,
                1.0, 3.0, 0.0
            },
            4,
            planeXCoeff: 1.0,
            planeYCoeff: 0.0,
            planeConstant: -1.0,
            slopeAngleDeg: 45.0);
    }

    private static double[] BuildGridVertices(int size, double spacing)
    {
        var vertices = new double[size * size * 3];
        int index = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                vertices[index * 3] = x * spacing;
                vertices[index * 3 + 1] = y * spacing;
                vertices[index * 3 + 2] = 0.0;
                index++;
            }
        }

        return vertices;
    }

    private static double[] BuildSlopedGridVertices(int size, double spacing, double xSlope, double ySlope = 0.0)
    {
        var vertices = new double[size * size * 3];
        int index = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double worldX = x * spacing;
                double worldY = y * spacing;
                vertices[index * 3] = worldX;
                vertices[index * 3 + 1] = worldY;
                vertices[index * 3 + 2] = (worldX * xSlope) + (worldY * ySlope);
                index++;
            }
        }

        return vertices;
    }

    private static double[] BuildRotatedRectangle(double centerX, double centerY, double width, double height, double angleDeg)
    {
        double angle = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        double[] local =
        [
            -halfWidth, -halfHeight,
            halfWidth, -halfHeight,
            halfWidth, halfHeight,
            -halfWidth, halfHeight
        ];

        var xy = new double[8];
        for (int i = 0; i < 4; i++)
        {
            double lx = local[i * 2];
            double ly = local[i * 2 + 1];
            xy[i * 2] = centerX + (lx * cos) - (ly * sin);
            xy[i * 2 + 1] = centerY + (lx * sin) + (ly * cos);
        }

        return xy;
    }

    private static double[] BuildRegularPolygon(double centerX, double centerY, double radius, int vertexCount)
    {
        var xy = new double[vertexCount * 2];
        for (int i = 0; i < vertexCount; i++)
        {
            double angle = (Math.PI * 2.0 * i) / vertexCount;
            xy[i * 2] = centerX + Math.Cos(angle) * radius;
            xy[i * 2 + 1] = centerY + Math.Sin(angle) * radius;
        }

        return xy;
    }

    private static int CountInteriorNakedEdges(
        GradingResult result,
        double minX,
        double maxX,
        double minY,
        double maxY,
        double tolerance)
    {
        var edgeFaceCount = new Dictionary<(int A, int B), int>();
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            int a = result.Faces[faceIndex * 3];
            int b = result.Faces[faceIndex * 3 + 1];
            int c = result.Faces[faceIndex * 3 + 2];
            AddEdge(a, b);
            AddEdge(b, c);
            AddEdge(c, a);
        }

        int count = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = pair.Key.A;
            int b = pair.Key.B;
            double mx = (result.Vertices[a * 3] + result.Vertices[b * 3]) * 0.5;
            double my = (result.Vertices[a * 3 + 1] + result.Vertices[b * 3 + 1]) * 0.5;
            bool onOuterBoundary =
                Math.Abs(mx - minX) <= tolerance ||
                Math.Abs(mx - maxX) <= tolerance ||
                Math.Abs(my - minY) <= tolerance ||
                Math.Abs(my - maxY) <= tolerance;
            if (!onOuterBoundary)
                count++;
        }

        return count;

        void AddEdge(int a, int b)
        {
            if (a > b)
                (a, b) = (b, a);
            var key = (a, b);
            edgeFaceCount[key] = edgeFaceCount.TryGetValue(key, out int current) ? current + 1 : 1;
        }
    }

    private static List<double> CollectBatterTriangleSlopeAngles(
        GradingResult result,
        double minimumZ,
        double maximumZ,
        Func<double, double, bool>? includeCentroid = null)
    {
        var angles = new List<double>();
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            int a = result.Faces[faceIndex * 3];
            int b = result.Faces[faceIndex * 3 + 1];
            int c = result.Faces[faceIndex * 3 + 2];
            double centroidX = (result.Vertices[a * 3] + result.Vertices[b * 3] + result.Vertices[c * 3]) / 3.0;
            double centroidY = (result.Vertices[a * 3 + 1] + result.Vertices[b * 3 + 1] + result.Vertices[c * 3 + 1]) / 3.0;
            if (includeCentroid != null && !includeCentroid(centroidX, centroidY))
                continue;

            double centroidZ = (result.Vertices[a * 3 + 2] + result.Vertices[b * 3 + 2] + result.Vertices[c * 3 + 2]) / 3.0;
            if (centroidZ <= minimumZ || centroidZ >= maximumZ)
                continue;

            if (TryComputeTriangleSlopeDeg(result.Vertices, a, b, c, out double slopeDeg))
                angles.Add(slopeDeg);
        }

        return angles;
    }

    private static bool TryComputeTriangleSlopeDeg(double[] vertices, int a, int b, int c, out double slopeDeg)
    {
        slopeDeg = 0.0;
        double ax = vertices[a * 3];
        double ay = vertices[a * 3 + 1];
        double az = vertices[a * 3 + 2];
        double ux = vertices[b * 3] - ax;
        double uy = vertices[b * 3 + 1] - ay;
        double uz = vertices[b * 3 + 2] - az;
        double vx = vertices[c * 3] - ax;
        double vy = vertices[c * 3 + 1] - ay;
        double vz = vertices[c * 3 + 2] - az;
        double nx = (uy * vz) - (uz * vy);
        double ny = (uz * vx) - (ux * vz);
        double nz = (ux * vy) - (uy * vx);
        double normalLengthSquared = (nx * nx) + (ny * ny) + (nz * nz);
        if (normalLengthSquared <= 1e-16)
            return false;

        slopeDeg = Math.Atan2(Math.Sqrt((nx * nx) + (ny * ny)), Math.Abs(nz)) * 180.0 / Math.PI;
        return true;
    }

    private static int[] BuildGridFaces(int size)
    {
        var faces = new List<int>((size - 1) * (size - 1) * 6);
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                int a = y * size + x;
                int b = a + 1;
                int c = a + size;
                int d = c + 1;

                faces.Add(a);
                faces.Add(b);
                faces.Add(d);

                faces.Add(a);
                faces.Add(d);
                faces.Add(c);
            }
        }

        return faces.ToArray();
    }

    private static int[] BuildSquareFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }

    private static int FindVertexIndex(double[] vertices, int vertexCount, double x, double y)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= 1e-6 &&
                Math.Abs(vertices[i * 3 + 1] - y) <= 1e-6)
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not find vertex at ({x}, {y}).");
    }

    private static bool ContainsVertex(double[] vertices, double x, double y)
    {
        int vertexCount = vertices.Length / 3;
        for (int i = 0; i < vertexCount; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= 1e-6 &&
                Math.Abs(vertices[i * 3 + 1] - y) <= 1e-6)
            {
                return true;
            }
        }

        return false;
    }

    private static int[] NormalizeFaces(int[] faces, int faceCount)
    {
        var normalized = new (int a, int b, int c)[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            int a = faces[i * 3];
            int b = faces[i * 3 + 1];
            int c = faces[i * 3 + 2];
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            normalized[i] = (a, b, c);
        }

        Array.Sort(normalized, static (left, right) =>
        {
            int compare = left.a.CompareTo(right.a);
            if (compare != 0)
                return compare;

            compare = left.b.CompareTo(right.b);
            if (compare != 0)
                return compare;

            return left.c.CompareTo(right.c);
        });

        var flattened = new int[faceCount * 3];
        for (int i = 0; i < normalized.Length; i++)
        {
            flattened[i * 3] = normalized[i].a;
            flattened[i * 3 + 1] = normalized[i].b;
            flattened[i * 3 + 2] = normalized[i].c;
        }

        return flattened;
    }

    // ── Barrier clipping tests ────────────────────────────────────────────────

    private static PadGrader.LockCurve MakeLockCurve(double x0, double y0, double x1, double y1)
    {
        return new PadGrader.LockCurve(new[] { x0, y0, x1, y1 }, 2);
    }

    [Fact]
    public void Grade_PadWithLockCurveInsideShoulderZone_TriangulatesSuccessfully()
    {
        // 100×100 flat terrain. Pad 20×20 centred at (50,50). Lock curve at y=62 cuts through the shoulder.
        double[] vertices =
        {
            0.0,   0.0,   0.0,
            100.0, 0.0,   0.0,
            100.0, 100.0, 0.0,
            0.0,   100.0, 0.0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var pad = new PadGrader.PadBoundary(
            new[]
            {
                40.0, 40.0,
                60.0, 40.0,
                60.0, 60.0,
                40.0, 60.0
            },
            4,
            targetZ: 1.0,
            slopeAngleDeg: 45.0,
            maxDistance: 15.0);

        var lockCurve = MakeLockCurve(0.0, 65.0, 100.0, 65.0);

        GradingResult? result = PadGrader.Grade(
            vertices, vertices.Length / 3,
            faces, faces.Length / 3,
            new[] { pad },
            new[] { lockCurve },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);
    }

    [Fact]
    public void Grade_PadWithLockCurveInsideShoulderZone_ShoulderRingClippedAtLockCurve()
    {
        // Same setup: shoulder ring should not produce vertices north of the lock curve.
        double[] vertices =
        {
            0.0,   0.0,   0.0,
            100.0, 0.0,   0.0,
            100.0, 100.0, 0.0,
            0.0,   100.0, 0.0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var pad = new PadGrader.PadBoundary(
            new[]
            {
                40.0, 40.0,
                60.0, 40.0,
                60.0, 60.0,
                40.0, 60.0
            },
            4,
            targetZ: 1.0,
            slopeAngleDeg: 45.0,
            maxDistance: 15.0);

        var lockCurve = MakeLockCurve(0.0, 65.0, 100.0, 65.0);

        GradingResult? result = PadGrader.Grade(
            vertices, vertices.Length / 3,
            faces, faces.Length / 3,
            new[] { pad },
            new[] { lockCurve },
            out _);

        Assert.NotNull(result);

        const double barrierY = 65.0;
        const double tol = 0.1;
        int vertCount = result!.VertexCount;
        double[] verts = result.Vertices;
        for (int i = 0; i < vertCount; i++)
        {
            double vy = verts[i * 3 + 1];
            Assert.True(vy <= barrierY + tol || vy >= 100.0 - tol,
                $"Vertex {i} at y={vy:F3} is north of lock-curve barrier at y={barrierY} but not at terrain boundary.");
        }
    }

    [Fact]
    public void Grade_PadWithNoLockCurves_BehaviorUnchanged()
    {
        // Regression: pad without lock curves should still grade successfully.
        double[] vertices =
        {
            0.0,   0.0,   0.0,
            100.0, 0.0,   0.0,
            100.0, 100.0, 0.0,
            0.0,   100.0, 0.0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var pad = new PadGrader.PadBoundary(
            new[]
            {
                40.0, 40.0,
                60.0, 40.0,
                60.0, 60.0,
                40.0, 60.0
            },
            4,
            targetZ: 1.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        GradingResult? result = PadGrader.Grade(
            vertices, vertices.Length / 3,
            faces, faces.Length / 3,
            new[] { pad },
            null,
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);
    }
}
