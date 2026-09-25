using MoleHill.Core.Analysis;
using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class TerrainUnitScalerTests
{
    [Fact]
    public void Scale_MixedTerrain_ScalesDimensionalValuesAndPreservesRatios()
    {
        var samples = new float[SculptDisplacementField.TileSize * SculptDisplacementField.TileSize];
        samples[0] = 2.0f;
        var sculpt = new SculptModifierDefinition
        {
            DetailSize = 0.25,
            CellSize = 0.125,
            ConstraintFeather = 0.5,
            Tiles =
            [
                new SculptTile
                {
                    I = 3,
                    J = -2,
                    D = Convert.ToBase64String(SculptDisplacementField.EncodeTile(samples))
                }
            ]
        };
        var placement = new TerrainObjectPlacementState
        {
            LastAppliedTransform =
            [
                1, 0, 0, 7,
                0, 1, 0, 8,
                0, 0, 1, 9,
                0, 0, 0, 1
            ]
        };
        var scatter = new ScatterObjectDefinition
        {
            ZOffset = 1,
            PerAreaDensity = 8,
            Spacing = 2,
            EdgeGap = 3,
            JitterXy = 4,
            ElevationMin = 5,
            ElevationMax = 6,
            RandomScaleMin = 0.75,
            PlacementStates = [placement]
        };
        var section = new CrossSectionStationAnnotationDefinition
        {
            InsertionOriginX = 1,
            InsertionOriginY = 2,
            InsertionOriginZ = 3,
            TextHeight = 0.5,
            StationInterval = 10,
            CrossSectionWidth = 20,
            GridCellWidth = 30,
            GridCellHeight = 40,
            ElevationGridInterval = 5,
            VerticalExaggeration = 2
        };
        var cutFill = new CutFillAnalysisDefinition
        {
            RangeLow = -2,
            RangeHigh = 3,
            ColorInterval = 0.5,
            DeltaContourInterval = 0.5
        };
        var aspect = new AspectAnalysisDefinition { FlatSlopeThresholdDegrees = 2.0 };
        var terrain = new TerrainDefinition
        {
            GlobalTolerance = 0.25,
            Modifiers =
            [
                new GradePathModifierDefinition { Width = 2, MaxDistance = 4, MaxEdgeDistance = 12, SlopeAngle = 33 },
                new MeshAreasModifierDefinition { MaxArea = 5, MinAngle = 20 },
                new RemeshModifierDefinition { EdgeLength = 2, MaxArea = 3, CreaseAngle = 45 },
                new RetainingWallModifierDefinition { MaxWallWidth = 1 },
                sculpt
            ],
            Objects = [scatter],
            Analyses = [cutFill, aspect],
            Annotations = [section]
        };
        terrain.LastAnalysisResults.Add(new TerrainAnalysisSummary
        {
            AnalysisId = cutFill.Id,
            SurfaceArea = 2,
            ElevationMinZ = 3,
            ElevationMaxZ = 4,
            CutFillDisplayAbsMax = 5,
            CutVolume = 6,
            FillVolume = 7,
            NetVolume = -1,
            ContourFirstLevel = 8,
            ContourLastLevel = 9,
            SampleMinValue = -2,
            SampleMaxValue = 3,
            SampleAverageValue = 0.5
        });

        TerrainUnitScaler.Scale([terrain], 10.0);

        Assert.Equal(2.5, terrain.GlobalTolerance);
        var path = Assert.IsType<GradePathModifierDefinition>(terrain.Modifiers[0]);
        Assert.Equal(20, path.Width);
        Assert.Equal(40, path.MaxDistance);
        Assert.Equal(120, path.MaxEdgeDistance);
        Assert.Equal(33, path.SlopeAngle);
        Assert.Equal(500, Assert.IsType<MeshAreasModifierDefinition>(terrain.Modifiers[1]).MaxArea);
        Assert.Equal(300, Assert.IsType<RemeshModifierDefinition>(terrain.Modifiers[2]).MaxArea);
        Assert.Equal(10, Assert.IsType<RetainingWallModifierDefinition>(terrain.Modifiers[3]).MaxWallWidth);
        Assert.Equal(2.5, sculpt.DetailSize);
        Assert.Equal(1.25, sculpt.CellSize);
        Assert.Equal(5.0, sculpt.ConstraintFeather);
        float[] scaledSamples = SculptDisplacementField.DecodeTile(Convert.FromBase64String(sculpt.Tiles[0].D));
        Assert.Equal(20.0f, scaledSamples[0]);

        Assert.Equal(10, scatter.ZOffset);
        Assert.Equal(0.08, scatter.PerAreaDensity, 10);
        Assert.Equal(20, scatter.Spacing);
        Assert.Equal(70, placement.LastAppliedTransform[3]);
        Assert.Equal(80, placement.LastAppliedTransform[7]);
        Assert.Equal(90, placement.LastAppliedTransform[11]);
        Assert.Equal(0.75, scatter.RandomScaleMin);

        Assert.Equal(10, section.InsertionOriginX);
        Assert.Equal(100, section.StationInterval);
        Assert.Equal(2, section.VerticalExaggeration);
        Assert.Equal(-20, cutFill.RangeLow);
        Assert.Equal(30, cutFill.RangeHigh);
        Assert.Equal(5, cutFill.ColorInterval);
        Assert.Equal(5, cutFill.DeltaContourInterval);

        // Aspect is angles and bearings throughout, so a unit change must leave it completely alone —
        // a flat threshold of 2° means the same thing in millimetres as in miles.
        Assert.Equal(2.0, aspect.FlatSlopeThresholdDegrees);
        Assert.Equal(0.0, aspect.RangeLow);
        Assert.Equal(AnalysisRange.FullTurnDegrees, aspect.RangeHigh);
        Assert.Equal(45.0, aspect.ColorInterval);

        TerrainAnalysisSummary summary = terrain.LastAnalysisResults[0];
        Assert.Equal(200, summary.SurfaceArea);
        Assert.Equal(6000, summary.CutVolume);
        Assert.Equal(-20, summary.SampleMinValue);
        Assert.Equal(30, summary.SampleMaxValue);
    }

    [Fact]
    public void Scale_ProjectToAndReportTable_ScalesLengthsAndKeepsTextMultiples()
    {
        var projectTo = new ProjectToModifierDefinition { FeatherDistance = 2, Strength = 0.5 };
        var reportTable = new ReportTableAnnotationDefinition
        {
            InsertionOriginX = 1,
            InsertionOriginY = 2,
            InsertionOriginZ = 3,
            TextHeight = 0.5,
            ColumnGap = 1.5,
            RowSpacing = 1.8
        };
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(projectTo);
        terrain.Annotations.Add(reportTable);

        TerrainUnitScaler.Scale(new[] { terrain }, 10);

        Assert.Equal(20, projectTo.FeatherDistance);
        Assert.Equal(0.5, projectTo.Strength);
        Assert.Equal(10, reportTable.InsertionOriginX);
        Assert.Equal(20, reportTable.InsertionOriginY);
        Assert.Equal(30, reportTable.InsertionOriginZ);
        Assert.Equal(5, reportTable.TextHeight);
        Assert.Equal(1.5, reportTable.ColumnGap);
        Assert.Equal(1.8, reportTable.RowSpacing);
    }
}
