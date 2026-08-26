using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Scales every persisted value expressed in model length, area, volume, or inverse area when Rhino
/// scales a document while changing its model units. Angles, percentages, counts, ratios, normalized
/// vectors, block scale factors, and paper-space plot weights are intentionally unchanged.
/// </summary>
internal static class TerrainUnitScaler
{
    public static void Scale(ModifierDefinition modifier, double lengthScale)
    {
        ValidateScale(lengthScale);
        ScaleModifier(modifier, lengthScale, lengthScale * lengthScale);
    }

    public static void Scale(AnalysisDefinition analysis, double lengthScale)
    {
        ValidateScale(lengthScale);
        ScaleAnalysis(analysis, lengthScale);
    }

    public static void Scale(TerrainObjectDefinition terrainObject, double lengthScale)
    {
        ValidateScale(lengthScale);
        ScaleObject(terrainObject, lengthScale, 1.0 / (lengthScale * lengthScale));
    }

    public static void Scale(IEnumerable<TerrainDefinition> terrains, double lengthScale)
    {
        ValidateScale(lengthScale);

        double areaScale = lengthScale * lengthScale;
        double volumeScale = areaScale * lengthScale;
        double inverseAreaScale = 1.0 / areaScale;

        foreach (TerrainDefinition terrain in terrains)
        {
            terrain.GlobalTolerance *= lengthScale;

            foreach (ModifierDefinition modifier in terrain.Modifiers)
                ScaleModifier(modifier, lengthScale, areaScale);

            foreach (TerrainObjectDefinition terrainObject in terrain.Objects)
                ScaleObject(terrainObject, lengthScale, inverseAreaScale);

            foreach (AnalysisDefinition analysis in terrain.Analyses)
                ScaleAnalysis(analysis, lengthScale);

            foreach (TerrainAnalysisSummary summary in terrain.LastAnalysisResults)
                ScaleSummary(terrain, summary, lengthScale, areaScale, volumeScale);

            if (terrain.LegacyLastAnalysis != null)
                ScaleSummary(terrain, terrain.LegacyLastAnalysis, lengthScale, areaScale, volumeScale);
        }
    }

    private static void ValidateScale(double lengthScale)
    {
        if (!double.IsFinite(lengthScale) || lengthScale <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(lengthScale));
    }

    private static void ScaleModifier(ModifierDefinition modifier, double lengthScale, double areaScale)
    {
        if (modifier is GeometryInputModifierDefinition geometryInput)
        {
            geometryInput.Tolerance *= lengthScale;
            geometryInput.MaxBoundaryEdgeLength *= lengthScale;
        }

        switch (modifier)
        {
            case GradePadModifierDefinition gradePad:
                gradePad.MaxDistance *= lengthScale;
                break;
            case GradePathModifierDefinition gradePath:
                gradePath.Width *= lengthScale;
                gradePath.MaxDistance *= lengthScale;
                break;
            case InSituStairModifierDefinition stair:
                stair.RiserHeight *= lengthScale;
                stair.MinTreadDepth *= lengthScale;
                stair.MaxDistance *= lengthScale;
                break;
            case MeshAreasModifierDefinition meshAreas:
                meshAreas.MaxArea *= areaScale;
                break;
            case RemeshModifierDefinition remesh:
                remesh.EdgeLength *= lengthScale;
                remesh.MaxArea *= areaScale;
                break;
            case RetainingWallModifierDefinition retainingWall:
                retainingWall.MaxWallWidth *= lengthScale;
                break;
            case RetopoModifierDefinition retopo:
                retopo.TargetEdgeLength *= lengthScale;
                break;
            case SculptModifierDefinition sculpt:
                ScaleSculpt(sculpt, lengthScale);
                break;
        }
    }

    private static void ScaleObject(
        TerrainObjectDefinition terrainObject,
        double lengthScale,
        double inverseAreaScale)
    {
        terrainObject.ZOffset *= lengthScale;
        foreach (TerrainObjectPlacementState placement in terrainObject.PlacementStates)
            ScalePlacementTranslation(placement, lengthScale);

        if (terrainObject is not ScatterObjectDefinition scatter)
            return;

        scatter.PerAreaDensity *= inverseAreaScale;
        scatter.Spacing *= lengthScale;
        scatter.EdgeGap *= lengthScale;
        scatter.JitterXy *= lengthScale;
        scatter.ElevationMin *= lengthScale;
        scatter.ElevationMax *= lengthScale;
    }

    private static void ScaleAnalysis(AnalysisDefinition analysis, double lengthScale)
    {
        if (analysis is ElevationAnalysisDefinition or CutFillAnalysisDefinition)
        {
            analysis.RangeLow *= lengthScale;
            analysis.RangeHigh *= lengthScale;
            analysis.ColorInterval *= lengthScale;
        }

        switch (analysis)
        {
            case ContourAnalysisDefinition contour:
                contour.Interval *= lengthScale;
                contour.StartZ *= lengthScale;
                contour.LabelInterval *= lengthScale;
                contour.LabelTextHeight *= lengthScale;
                break;
            case CurveElevationLabelAnalysisDefinition curveElevation:
                curveElevation.Interval *= lengthScale;
                break;
            case CurveSlopeLabelAnalysisDefinition curveSlope:
                curveSlope.Interval *= lengthScale;
                break;
            case SlopeArrowAnalysisDefinition slopeArrow:
                slopeArrow.GridSpacing *= lengthScale;
                break;
            case WaterflowAnalysisDefinition waterflow:
                waterflow.MaxLength *= lengthScale;
                break;
            case GradeBetweenPointsAnalysisDefinition grade:
                grade.TextHeight *= lengthScale;
                break;
            case CrossSectionStationAnalysisDefinition crossSection:
                ScaleSection(crossSection, lengthScale);
                crossSection.StationInterval *= lengthScale;
                crossSection.CrossSectionWidth *= lengthScale;
                crossSection.GridCellWidth *= lengthScale;
                crossSection.GridCellHeight *= lengthScale;
                crossSection.ElevationGridInterval *= lengthScale;
                break;
            case LongitudinalSectionAnalysisDefinition longitudinal:
                ScaleSection(longitudinal, lengthScale);
                longitudinal.SampleInterval *= lengthScale;
                longitudinal.ElevationGridInterval *= lengthScale;
                longitudinal.StationLabelInterval *= lengthScale;
                break;
            case TerrainSectionAnalysisDefinition section:
                ScaleSection(section, lengthScale);
                section.StationTickInterval *= lengthScale;
                section.ElevationGridInterval *= lengthScale;
                break;
            case TerrainSectionAnalysisDefinitionBase sectionBase:
                ScaleSection(sectionBase, lengthScale);
                break;
        }
    }

    private static void ScaleSection(TerrainSectionAnalysisDefinitionBase section, double lengthScale)
    {
        section.InsertionOriginX *= lengthScale;
        section.InsertionOriginY *= lengthScale;
        section.InsertionOriginZ *= lengthScale;
        section.TextHeight *= lengthScale;
    }

    private static void ScaleSummary(
        TerrainDefinition terrain,
        TerrainAnalysisSummary summary,
        double lengthScale,
        double areaScale,
        double volumeScale)
    {
        summary.SurfaceArea *= areaScale;
        summary.ElevationMinZ *= lengthScale;
        summary.ElevationMaxZ *= lengthScale;
        summary.CutFillDisplayAbsMax *= lengthScale;
        summary.CutVolume *= volumeScale;
        summary.FillVolume *= volumeScale;
        summary.NetVolume *= volumeScale;
        summary.ContourFirstLevel *= lengthScale;
        summary.ContourLastLevel *= lengthScale;

        AnalysisDefinition? analysis = terrain.Analyses.FirstOrDefault(item => item.Id == summary.AnalysisId);
        if (analysis is ElevationAnalysisDefinition or CutFillAnalysisDefinition or
            ProjectedElevationLabelAnalysisDefinition or CurveElevationLabelAnalysisDefinition)
        {
            summary.SampleMinValue *= lengthScale;
            summary.SampleMaxValue *= lengthScale;
            summary.SampleAverageValue *= lengthScale;
        }
    }

    private static void ScalePlacementTranslation(TerrainObjectPlacementState placement, double lengthScale)
    {
        double[] values = placement.LastAppliedTransform;
        if (values == null || values.Length != 16)
            return;

        values[3] *= lengthScale;
        values[7] *= lengthScale;
        values[11] *= lengthScale;
    }

    private static void ScaleSculpt(SculptModifierDefinition sculpt, double lengthScale)
    {
        sculpt.DetailSize *= lengthScale;
        sculpt.CellSize *= lengthScale;
        sculpt.ConstraintFeather *= lengthScale;
        if (sculpt.Tiles.Count == 0)
            return;

        try
        {
            var field = SculptFieldCodec.Decode(sculpt);
            foreach (float[] samples in field.Tiles.Values)
            {
                for (int index = 0; index < samples.Length; index++)
                    samples[index] = (float)(samples[index] * lengthScale);
            }

            sculpt.Tiles = SculptFieldCodec.Encode(field);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid legacy cell size: the build codec will apply its normal validation and recovery.
        }
    }
}
