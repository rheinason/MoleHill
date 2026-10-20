using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.UI;

/// <summary>Optional scan preparation rows and evidence; ordinary terrain extraction skips this stage.</summary>
internal sealed partial class TerrainCurveExtractionDialog
{
    private readonly DynamicLayout _scanAdvanced = new() { Spacing = new Eto.Drawing.Size(8, 7) };

    private bool BuildPreparationSections(DynamicLayout body)
    {
        if (Settings is not ScanToTerrainSettings settings) return false;
        var outputs = Section(body, "Outputs");
        Output(outputs, "Walls and terrace edges", nameof(settings.Walls));
        Output(outputs, "Paved and soft boundaries", nameof(settings.Surfaces));
        Output(outputs, "Kerbs and paving edges", nameof(settings.RoadEdges));
        Output(outputs, "Building context", nameof(settings.Buildings));
        Output(outputs, "Ground check mesh", nameof(settings.AddGroundMesh), false);
        var advanced = _scanAdvanced;
        string length = _units.Abbreviation;
        var ground = Section(advanced, "Ground");
        Number(ground, "Raster cell (0 = automatic) · " + length, nameof(settings.RasterCell), 0, 1e12);
        Number(ground, "Ground spacing · " + length, nameof(settings.GroundSpacing));
        Number(ground, "Largest object width · " + length, nameof(settings.LargestObject), _units.FromMeters(2));
        Number(ground, "Initial height allowance · " + length, nameof(settings.InitialThreshold));
        Number(ground, "Maximum height allowance · " + length, nameof(settings.MaximumThreshold));
        Number(ground, "Object margin · " + length, nameof(settings.ObjectMargin), 0);
        var slope = new TextBox
        {
            Text = SlopeInput.FormatWithUnit(settings.GroundSlope, SlopeUnitPreference.Current),
            Width = 140,
            ToolTip = SlopeInput.AcceptedFormatsHelp
        };
        void CommitSlope()
        {
            _inputError = null;
            if (SlopeInput.TryParse(slope.Text, SlopeUnitPreference.Current, out double value) && double.IsFinite(value) && value >= 0)
                settings.GroundSlope = value;
            else _inputError = "Enter a valid non-negative ground slope, such as 60% or 1:3.";
            Schedule();
        }
        slope.LostFocus += (_, _) => CommitSlope();
        slope.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { CommitSlope(); e.Handled = true; } };
        slope.TextChanged += (_, _) => { _keep.Enabled = false; };
        ground.AddRow(new Label { Text = "Ground slope allowance" }, slope);

        var roofs = Section(advanced, "Buildings");
        Number(roofs, "Minimum footprint · " + length + "²", nameof(settings.BuildingArea), _units.FromSquareMeters(.25), increment: _units.FromSquareMeters(1));
        Number(roofs, "Minimum height · " + length, nameof(settings.BuildingHeight));
        Number(roofs, "Roof fitting tolerance · " + length, nameof(settings.RoofTolerance));
        Number(roofs, "Minimum roof detail (0 = off) · " + length, nameof(settings.RoofDetail), 0);
        Number(roofs, "Maximum roof faces", nameof(settings.RoofFaces), 1, 200, 0);
        roofs.AddRow(new Label { Text = "Red roofs are approximate fallbacks. Simplification keeps the main roof form when possible; fit error is retained for review.", Wrap = WrapMode.Word, Width = 460 });
        var walls = Section(advanced, "Walls and terrace edges");
        Number(walls, "Minimum height · " + length, nameof(settings.WallHeight));
        Number(walls, "Minimum length · " + length, nameof(settings.WallLength));
        var areas = Section(advanced, "Paved and soft ground");
        areas.AddRow(new Label { Text = "Paved outlines are draped breaklines that carry the paving; soft outlines are drawings, and contours describe that ground.", Wrap = WrapMode.Word, Width = 460 });
        var samples = new CheckBox { Text = "Preview classification samples", Checked = false };
        _visibility["SurfaceClasses"] = samples;
        samples.CheckedChanged += (_, _) => UpdatePreview(); areas.AddRow(samples);
        Number(areas, "Minimum patch area · " + length + "²", nameof(settings.SurfaceArea), _units.FromSquareMeters(.1), increment: _units.FromSquareMeters(1));
        Number(areas, "Excess green threshold", nameof(settings.GreenThreshold), 0, 2, increment: .01);
        Number(areas, "Maximum paving saturation", nameof(settings.PavingSaturation), 0, 1, increment: .01);
        var roads = Section(advanced, "Kerbs and road edges");
        Number(roads, "Built-edge plan tolerance · " + length, nameof(settings.BuiltEdgeTolerance));
        Number(roads, "Maximum bridge length (0 = off) · " + length, nameof(settings.MaximumBridgeLength), 0);
        Number(ground, "Minimum noise feature (0 = off) · " + length, nameof(settings.NoiseFeatureSize), 0);
        Number(roads, "Road height tolerance · " + length, nameof(settings.RoadHeightTolerance));
        roads.AddRow(new Label { Text = "Road alignment is fitted in plan first. Height controls stay on those segments; measured kerbs keep their tighter profile tolerance.", Wrap = WrapMode.Word, Width = 460 });
        Number(roads, "Minimum kerb height · " + length, nameof(settings.KerbMin));
        Number(roads, "Maximum kerb height · " + length, nameof(settings.KerbMax));
        return true;
    }

    private string? PreparationError() => Settings is ScanToTerrainSettings scan &&
        (scan.KerbMax < scan.KerbMin || scan.MaximumThreshold < scan.InitialThreshold)
        ? "A maximum height must be at least its minimum height." : null;

    private static string PreparationSummary(TerrainCurveExtractionPrepared prepared, string lengthUnit)
    {
        if (prepared.Scan is not { } scan) return string.Empty;
        string classes = scan.Ground.ClassifiedGroundNodes == 0 ? string.Empty :
            $"Supplied ground: {scan.Ground.ClassifiedGroundNodes:N0} cells, {scan.Ground.ClassifiedGroundDisagreements:N0} filter disagreements. ";
        return $"{Count(scan.Buildings?.Buildings.Count ?? 0, "building")} " +
            $"({Count(scan.Buildings?.Buildings.Count(b => b.IsFallback) ?? 0, "fallback")}), " +
            $"{Count(scan.Walls.Count, "wall")}; {scan.RejectedFeatureCount} rejected fits, {scan.InferredFeatureLength:G3} {lengthUnit} inferred. " +
            $"Noise adjustment ≤ {scan.FilterMaximumDisplacement:G3} {lengthUnit}; filled {scan.Ground.FilledShare:P0}" +
            (scan.LoweredObjectArea > 0 ? $"; small objects lowered over {scan.LoweredObjectArea:F0} {lengthUnit}² (≤ {scan.LoweredObjectHeight:G3} {lengthUnit}). " : ". ") + classes;
    }
}
