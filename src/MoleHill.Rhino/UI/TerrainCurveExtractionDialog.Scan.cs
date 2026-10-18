using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.UI;

/// <summary>Optional scan preparation rows and evidence; ordinary terrain extraction skips this stage.</summary>
internal sealed partial class TerrainCurveExtractionDialog
{
    private bool BuildPreparationSections(DynamicLayout body)
    {
        if (Settings is not ScanToTerrainSettings settings) return false;
        string length = _units.Abbreviation;
        var ground = Section(body, "Ground");
        Number(ground, "Raster cell (0 = automatic) · " + length, nameof(settings.RasterCell), 0, 1e12);
        Number(ground, "Ground spacing · " + length, nameof(settings.GroundSpacing));
        Number(ground, "Largest object width · " + length, nameof(settings.LargestObject), _units.FromMeters(2));
        Number(ground, "Initial height allowance · " + length, nameof(settings.InitialThreshold));
        Number(ground, "Maximum height allowance · " + length, nameof(settings.MaximumThreshold));
        Number(ground, "Object margin · " + length, nameof(settings.ObjectMargin), 0);
        var slope = new TextBox { Text = SlopeInput.FormatWithUnit(settings.GroundSlope, SlopeUnitPreference.Current), Width = 140,
            ToolTip = SlopeInput.AcceptedFormatsHelp };
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
        Output(ground, "Check mesh", nameof(settings.AddGroundMesh), false);
        var roofs = Section(body, "Buildings"); Output(roofs, "Roof solids", nameof(settings.Buildings));
        Number(roofs, "Minimum footprint · " + length + "²", nameof(settings.BuildingArea), _units.FromSquareMeters(.25), increment: _units.FromSquareMeters(1));
        Number(roofs, "Minimum height · " + length, nameof(settings.BuildingHeight));
        Number(roofs, "Roof fitting tolerance · " + length, nameof(settings.RoofTolerance));
        var walls = Section(body, "Walls and terrace edges"); Output(walls, "Top and toe breaklines", nameof(settings.Walls));
        Number(walls, "Minimum height · " + length, nameof(settings.WallHeight));
        Number(walls, "Minimum length · " + length, nameof(settings.WallLength));
        var areas = Section(body, "Paved and soft ground"); Output(areas, "Area boundaries and colour split", nameof(settings.Surfaces));
        Number(areas, "Excess green threshold", nameof(settings.GreenThreshold), 0, 2, increment: .01);
        Number(areas, "Maximum paving saturation", nameof(settings.PavingSaturation), 0, 1, increment: .01);
        var roads = Section(body, "Kerbs and road edges"); Output(roads, "Measured kerbs or single breaklines", nameof(settings.RoadEdges));
        Number(roads, "Minimum kerb height · " + length, nameof(settings.KerbMin));
        Number(roads, "Maximum kerb height · " + length, nameof(settings.KerbMax));
        return true;
    }

    private string? PreparationError() => Settings is ScanToTerrainSettings scan &&
        (scan.KerbMax < scan.KerbMin || scan.MaximumThreshold < scan.InitialThreshold)
        ? "A maximum height must be at least its minimum height." : null;

    private static string PreparationSummary(TerrainCurveExtractionPrepared prepared)
    {
        if (prepared.Scan is not { } scan) return string.Empty;
        string classes = scan.Ground.ClassifiedGroundNodes == 0 ? string.Empty :
            $"Supplied ground: {scan.Ground.ClassifiedGroundNodes:N0} cells, {scan.Ground.ClassifiedGroundDisagreements:N0} filter disagreements. ";
        return $"{Count(scan.Buildings?.Buildings.Count ?? 0, "building")} " +
            $"({Count(scan.Buildings?.Buildings.Count(b => b.IsFallback) ?? 0, "fallback")}), " +
            $"{Count(scan.Walls.Count, "wall")}; filled {scan.Ground.FilledShare:P0}. " + classes;
    }
}
