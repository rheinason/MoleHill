using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

// One descriptor per concrete analysis/annotation type. Grouped in a single file for brevity; reflection
// discovery in AnalysisTypeRegistry treats each class independently. Metadata mirrors the former panel
// switches verbatim (kind/label/icon/accent/annotation/subtitle); discriminators are unchanged.

internal sealed class EarthworkAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "earthwork";
    public override Type DefinitionType => typeof(EarthworkAnalysisDefinition);
    public override string TypeLabel => "Earthworks";
    public override string MenuLabel => "Earthworks";
    public override string IconLabel => "EW";
    public override string? IconName => "AnEarthwork";
    public override int AccentArgb => unchecked((int)0xFF8D6E63);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Refs + summary";
    public override int SortOrder => 0;
    public override AnalysisDefinition Create() => new EarthworkAnalysisDefinition();
}

internal sealed class SlopeAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "slope";
    public override Type DefinitionType => typeof(SlopeAnalysisDefinition);
    public override string TypeLabel => "Slope";
    public override string MenuLabel => "Slope";
    public override string IconLabel => "%";
    public override string? IconName => "AnSlope";
    public override int AccentArgb => unchecked((int)0xFF43A047);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Slope preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 1;
    public override AnalysisDefinition Create() => new SlopeAnalysisDefinition();
}

internal sealed class ElevationAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "elevation";
    public override Type DefinitionType => typeof(ElevationAnalysisDefinition);
    public override string TypeLabel => "Elevation";
    public override string MenuLabel => "Elevation";
    public override string IconLabel => "Z";
    public override string? IconName => "AnElevation";
    public override int AccentArgb => unchecked((int)0xFF1E88E5);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Elevation preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 2;
    public override AnalysisDefinition Create() => new ElevationAnalysisDefinition();
}

internal sealed class CutFillAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "cut-fill";
    public override Type DefinitionType => typeof(CutFillAnalysisDefinition);
    public override string TypeLabel => "Cut / Fill";
    public override string MenuLabel => "Cut / Fill";
    public override string IconLabel => "+/-";
    public override string? IconName => "AnCutFill";
    public override int AccentArgb => unchecked((int)0xFFEF6C00);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Signed delta preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 3;
    public override AnalysisDefinition Create() => new CutFillAnalysisDefinition();
}

internal sealed class ContourAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "contour";
    public override Type DefinitionType => typeof(ContourAnalysisDefinition);
    public override string TypeLabel => "Contours";
    public override string MenuLabel => "Contours";
    public override string IconLabel => "CT";
    public override string? IconName => "AnContour";
    public override int AccentArgb => unchecked((int)0xFF00796B);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Contour lines at fixed intervals";
    public override int SortOrder => 0;
    public override AnalysisDefinition Create() => new ContourAnalysisDefinition();
}

internal sealed class CurveElevationLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "curve-elevation-label";
    public override Type DefinitionType => typeof(CurveElevationLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Heights (Curve)";
    public override string MenuLabel => "Spot Heights (Curve)";
    public override string IconLabel => "CE";
    public override string? IconName => "AnCurveElevation";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Elevation labels sampled along a curve";
    public override int SortOrder => 1;
    public override AnalysisDefinition Create() => new CurveElevationLabelAnalysisDefinition();
}

internal sealed class CurveSlopeLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "curve-slope-label";
    public override Type DefinitionType => typeof(CurveSlopeLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Slope (Curve)";
    public override string MenuLabel => "Spot Slope (Curve)";
    public override string IconLabel => "C%";
    public override string? IconName => "AnCurveSlope";
    public override int AccentArgb => unchecked((int)0xFF2E7D32);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Slope labels sampled along a curve";
    public override int SortOrder => 2;
    public override AnalysisDefinition Create() => new CurveSlopeLabelAnalysisDefinition();
}

internal sealed class ProjectedElevationLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "projected-elevation-label";
    public override Type DefinitionType => typeof(ProjectedElevationLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Heights (Points)";
    public override string MenuLabel => "Spot Heights (Points)";
    public override string IconLabel => "PZ";
    public override string? IconName => "AnProjElevation";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Elevation labels at picked points";
    public override int SortOrder => 3;
    public override AnalysisDefinition Create() => new ProjectedElevationLabelAnalysisDefinition();
}

internal sealed class PointSlopeLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "point-slope-label";
    public override Type DefinitionType => typeof(PointSlopeLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Slope (Points)";
    public override string MenuLabel => "Spot Slope (Points)";
    public override string IconLabel => "P%";
    public override string? IconName => "AnPointSlope";
    public override int AccentArgb => unchecked((int)0xFF0288D1);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Slope labels at picked points";
    public override int SortOrder => 4;
    public override AnalysisDefinition Create() => new PointSlopeLabelAnalysisDefinition();
}

internal sealed class SlopeArrowAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "slope-arrows";
    public override Type DefinitionType => typeof(SlopeArrowAnalysisDefinition);
    public override string TypeLabel => "Flow Arrows";
    public override string MenuLabel => "Flow Arrows";
    public override string IconLabel => "FA";
    public override string? IconName => "AnFlowArrows";
    public override int AccentArgb => unchecked((int)0xFF0288D1);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Downhill arrows on a grid";
    public override int SortOrder => 5;
    public override AnalysisDefinition Create() => new SlopeArrowAnalysisDefinition();
}

internal sealed class GradeBetweenPointsAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "grade-callout";
    public override Type DefinitionType => typeof(GradeBetweenPointsAnalysisDefinition);
    public override string TypeLabel => "Grade Callout";
    public override string MenuLabel => "Grade Callout";
    public override string IconLabel => "1:n";
    public override string? IconName => "AnGradeCallout";
    public override int AccentArgb => unchecked((int)0xFF00838F);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Grade between two points (1:n + %)";
    public override int SortOrder => 6;
    public override AnalysisDefinition Create() => new GradeBetweenPointsAnalysisDefinition();
}

internal sealed class TerrainSectionAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "terrain-section";
    public override Type DefinitionType => typeof(TerrainSectionAnalysisDefinition);
    public override string TypeLabel => "Section Cut";
    public override string MenuLabel => "Section Cut";
    public override string IconLabel => "TS";
    public override string? IconName => "AnTerrainSection";
    public override int AccentArgb => unchecked((int)0xFF7B1FA2);
    public override bool IsAnnotation => true;
    public override string Subtitle => "True-scale profile at the cut line";
    public override int SortOrder => 7;
    public override AnalysisDefinition Create() => new TerrainSectionAnalysisDefinition();
}

internal sealed class CrossSectionStationAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "cross-section-station";
    public override Type DefinitionType => typeof(CrossSectionStationAnalysisDefinition);
    public override string TypeLabel => "Cross-Sections";
    public override string MenuLabel => "Cross-Sections";
    public override string IconLabel => "XS";
    public override string? IconName => "AnCrossSection";
    public override int AccentArgb => unchecked((int)0xFF8E24AA);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Unrolled cuts at stations, in a grid";
    public override int SortOrder => 8;
    public override AnalysisDefinition Create() => new CrossSectionStationAnalysisDefinition();
}

internal sealed class LongitudinalSectionAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "longitudinal-section";
    public override Type DefinitionType => typeof(LongitudinalSectionAnalysisDefinition);
    public override string TypeLabel => "Section Along Curve";
    public override string MenuLabel => "Section Along Curve";
    public override string IconLabel => "LS";
    public override string? IconName => "AnLongSection";
    public override int AccentArgb => unchecked((int)0xFF5E35B1);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Unrolled profile that follows a curve";
    public override int SortOrder => 9;
    public override AnalysisDefinition Create() => new LongitudinalSectionAnalysisDefinition();
}
