using System;
using System.Globalization;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Pins the collapsed-card text of every modifier type. The expected strings were written from the
/// panel's former hand-written switch before it moved into <see cref="ModifierTypeDescriptor.Summarize"/>.
/// </summary>
public sealed class ModifierSummaryTests
{
    private static SourceReferenceSet Refs(int objects, int layers = 0)
    {
        var set = new SourceReferenceSet();
        for (int i = 0; i < objects; i++) set.ObjectIds.Add(Guid.NewGuid());
        for (int i = 0; i < layers; i++) set.LayerPaths.Add($"Layer {i}");
        return set;
    }

    // The summaries format with the current culture (as the panel always did); pin it so the expectations hold anywhere.
    private static string Summary(ModifierDefinition modifier)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return TerrainTypeRegistry.ForModifierType(modifier.GetType())!.Summarize(modifier); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Triangulate_Summary_CountsEachSourceSet() =>
        Assert.Equal("1 DEM | 5 points | 2 breaklines | 0 contours", Summary(new TriangulateModifierDefinition
        {
            DemSurface = Refs(1), Points = Refs(3, 2), Breaklines = Refs(1, 1), Contours = Refs(0)
        }));

    [Fact]
    public void AddGeometry_Summary_CountsEachSourceSet() =>
        Assert.Equal("2 points | 1 breaklines | 3 contours", Summary(new AddGeometryModifierDefinition
        {
            Points = Refs(2), Breaklines = Refs(0, 1), Contours = Refs(3)
        }));

    [Fact]
    public void Remesh_Summary_AutoEdgeWithoutCrease() =>
        Assert.Equal("Edge Length auto", Summary(new RemeshModifierDefinition { EdgeLength = 0, CreaseAngle = 0 }));

    [Fact]
    public void Remesh_Summary_EdgeAndCrease() =>
        Assert.Equal("Edge Length 2.5 | Crease Angle 30 deg",
            Summary(new RemeshModifierDefinition { EdgeLength = 2.5, CreaseAngle = 30 }));

    [Fact]
    public void Retopo_Summary_EdgeAndQuads()
    {
        Assert.Equal("Edge Length 1.5 | Quads on", Summary(new RetopoModifierDefinition { TargetEdgeLength = 1.5, Quads = true }));
        Assert.Equal("Edge Length auto | field preview", Summary(new RetopoModifierDefinition { TargetEdgeLength = 0, Quads = false }));
    }

    [Fact]
    public void Simplify_Summary_PerMode()
    {
        Assert.Equal("At most 12,000 vertices", Summary(new SimplifyModifierDefinition
        { Mode = SimplifyModifierDefinition.TargetVertexCountMode, TargetVertexCount = 12000 }));
        Assert.Equal("Retain 40%", Summary(new SimplifyModifierDefinition
        { Mode = SimplifyModifierDefinition.RetainPercentageMode, RetainPercentage = 40 }));
        Assert.Equal("Max Deviation 0.25", Summary(new SimplifyModifierDefinition
        { Mode = "other", MaximumDeviation = 0.25 }));
    }

    [Fact]
    public void Smooth_Summary_IterationsStrengthAndProtect() =>
        Assert.Equal("5 iterations | Strength 0.5 | 2 protect curves", Summary(new SmoothModifierDefinition
        { Iterations = 5, Strength = 0.5, Breaklines = Refs(2) }));

    [Fact]
    public void Sculpt_Summary_EmptyAndWithTiles()
    {
        Assert.Equal("no strokes", Summary(new SculptModifierDefinition()));
        var sculpt = new SculptModifierDefinition { Constraints = Refs(1) };
        sculpt.Tiles.Add(new SculptTile());
        sculpt.Tiles.Add(new SculptTile { I = 1 });
        Assert.Equal("2 tiles | 1 protect curves", Summary(sculpt));
    }

    [Fact]
    public void ProjectTo_Summary_TargetKinds()
    {
        Assert.Equal("no target | 1 boundaries | Strength 1",
            Summary(new ProjectToModifierDefinition { Boundaries = Refs(1), Strength = 1 }));
        Assert.Equal("target terrain | 0 boundaries | Strength 0.5",
            Summary(new ProjectToModifierDefinition { TargetTerrainId = Guid.NewGuid(), Strength = 0.5 }));
        Assert.Equal("target mesh | 0 boundaries | Strength 0.5",
            Summary(new ProjectToModifierDefinition { TargetMesh = Refs(1), TargetTerrainId = Guid.NewGuid(), Strength = 0.5 }));
    }

    [Fact]
    public void GradePad_Summary_UsesSlopeUnit() =>
        Assert.Equal("2 boundaries | Fill 100%", Summary(new GradePadModifierDefinition
        { Boundaries = Refs(2), SlopeAngle = 45 }));

    [Fact]
    public void GradePath_Summary_FixedAndVariableWidth()
    {
        Assert.Equal("2 centerlines | Width 3",
            Summary(new GradePathModifierDefinition { Paths = Refs(2), Width = 3 }));
        Assert.Equal("1 centerlines | variable width, 3 width edges | Width 3 fallback",
            Summary(new GradePathModifierDefinition { Paths = Refs(1), UseVariableWidth = true, WidthEdges = Refs(3), Width = 3 }));
    }

    [Fact]
    public void GradeLine_Summary_SymmetricAndAsymmetric()
    {
        Assert.Equal("2 design lines | Fill 100%",
            Summary(new GradeLineModifierDefinition { Lines = Refs(2), SlopeAngle = 45 }));
        Assert.Equal("2 design lines | asymmetric sides",
            Summary(new GradeLineModifierDefinition { Lines = Refs(2), UseAsymmetricSides = true }));
    }

    [Fact]
    public void RetainingWall_Summary_Modes()
    {
        Assert.Equal("2 wall curves | grade terrain, Fill 100%",
            Summary(new RetainingWallModifierDefinition { WallCurves = Refs(2), Mode = RetainingWallModifierDefinition.GradeMode, SlopeAngle = 45 }));
        Assert.Equal("2 wall curves | grade terrain, asymmetric sides",
            Summary(new RetainingWallModifierDefinition { WallCurves = Refs(2), Mode = RetainingWallModifierDefinition.GradeMode, UseAsymmetricSides = true }));
        Assert.Equal("2 wall curves | breaklines only",
            Summary(new RetainingWallModifierDefinition { WallCurves = Refs(2), Mode = RetainingWallModifierDefinition.BreaklineOnlyMode }));
    }

    [Fact]
    public void InSituStair_Summary_ComputedAndReference()
    {
        Assert.Equal("1 reference surfaces | Riser Height 0.17",
            Summary(new InSituStairModifierDefinition { ReferenceSurface = Refs(1), RiserHeight = 0.17 }));
        Assert.Equal("4 surfaces | Tread Depth 0.3",
            Summary(new InSituStairModifierDefinition
            { ReferenceSurface = Refs(1), ComputedSurfaceCount = 4, ComputedTreadDepthSummary = "0.3" }));
        Assert.Equal("1 surfaces | Tread Depth 0.3",
            Summary(new InSituStairModifierDefinition { ReferenceSurface = Refs(1), ComputedTreadDepthSummary = "0.3" }));
    }

    [Fact]
    public void ModifierDescriptors_AllCoveredBySummaryTests()
    {
        // 13 types are covered above; a new modifier type must add a case here.
        Assert.Equal(13, TerrainTypeRegistry.Modifiers.Count);
    }
}
