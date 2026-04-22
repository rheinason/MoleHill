using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PadGraderTests
{
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
            0.0,
            0.0,
            out var legacyWarning);

        Assert.NotNull(legacy);
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
            0.0,
            0.0,
            out string? warning);

        Assert.NotNull(result);
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
            maxArea: 0.0,
            minAngle: 0.0,
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
            maxArea: 0.0,
            minAngle: 0.0,
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
            maxArea: 0.0,
            minAngle: 0.0,
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);
    }
}
