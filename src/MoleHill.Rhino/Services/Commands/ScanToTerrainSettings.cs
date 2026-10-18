using MoleHill.Core.Scan;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>Physical defaults and model-unit settings for one scan preview. Cache is written on Keep.</summary>
internal sealed record ScanToTerrainSettings : TerrainCurveExtractionSettings
{
    public double LargestObject { get; set; }
    public double GroundSpacing { get; set; }
    public double RasterCell { get; set; }
    public double GroundSlope { get; set; } = .6;
    public double InitialThreshold { get; set; }
    public double MaximumThreshold { get; set; }
    public double ObjectMargin { get; set; }
    public bool AddGroundMesh { get; set; }
    public bool Walls { get; set; } = true;
    public double WallHeight { get; set; }
    public double WallLength { get; set; }
    public bool Surfaces { get; set; } = true;
    public bool RoadEdges { get; set; } = true;
    public double GreenThreshold { get; set; } = .08;
    public double PavingSaturation { get; set; } = .25;
    public double SurfaceArea { get; set; }
    public double KerbMin { get; set; }
    public double KerbMax { get; set; }
    public double RoadPlanTolerance { get; set; }
    public double RoadHeightTolerance { get; set; }
    public bool Buildings { get; set; } = true;
    public double BuildingArea { get; set; }
    public double BuildingHeight { get; set; }
    public double RoofTolerance { get; set; }
    public double RoofDetail { get; set; }
    public int RoofFaces { get; set; } = 12;

    private const string Prefix = "MoleHill.ScanToTerrain.";
    private static readonly (string Name, double Metres)[] Lengths =
    {
        (nameof(LargestObject), 48),
        (nameof(GroundSpacing), 1), (nameof(RasterCell), 0), (nameof(InitialThreshold), .3),
        (nameof(MaximumThreshold), 4), (nameof(ObjectMargin), .5), (nameof(WallHeight), .6),
        (nameof(WallLength), 3), (nameof(KerbMin), .06), (nameof(KerbMax), .3),
        (nameof(BuildingHeight), 2), (nameof(RoofTolerance), .3), (nameof(RoofDetail), 2),
        (nameof(RoadPlanTolerance), 1), (nameof(RoadHeightTolerance), .2),
    };
    private static readonly string[] Toggles = { nameof(Walls), nameof(Surfaces), nameof(RoadEdges), nameof(Buildings), nameof(AddGroundMesh) };

    public static ScanToTerrainSettings Load(ModelUnitContext units)
    {
        var result = Read(new ScanToTerrainSettings(), units, Prefix, .25, .5, 300, 2);
        foreach (var entry in Lengths)
            typeof(ScanToTerrainSettings).GetProperty(entry.Name)!.SetValue(result, CommandOptionCache.GetLength(Prefix + entry.Name, units, entry.Metres));
        foreach (string name in Toggles)
        {
            var property = typeof(ScanToTerrainSettings).GetProperty(name)!;
            property.SetValue(result, CommandOptionCache.GetValue(Prefix + name, (bool)property.GetValue(result)! ? 1 : 0) == 1);
        }
        result.BuildingArea = units.FromSquareMeters(CommandOptionCache.GetValue(Prefix + "BuildingArea.SquareMeters", 20.0));
        result.SurfaceArea = units.FromSquareMeters(CommandOptionCache.GetValue(Prefix + "SurfaceArea.SquareMeters", 10.0));
        result.GreenThreshold = CommandOptionCache.GetValue(Prefix + nameof(GreenThreshold), .08);
        result.PavingSaturation = CommandOptionCache.GetValue(Prefix + nameof(PavingSaturation), .25);
        result.GroundSlope = CommandOptionCache.GetValue(Prefix + nameof(GroundSlope), .6);
        result.RoofFaces = CommandOptionCache.GetValue(Prefix + nameof(RoofFaces), 12);
        return result;
    }

    public void Save(ModelUnitContext units)
    {
        Save(units, Prefix);
        foreach (var entry in Lengths)
            CommandOptionCache.SetLength(Prefix + entry.Name, units, (double)typeof(ScanToTerrainSettings).GetProperty(entry.Name)!.GetValue(this)!);
        foreach (string name in Toggles)
            CommandOptionCache.SetValue(Prefix + name, (bool)typeof(ScanToTerrainSettings).GetProperty(name)!.GetValue(this)! ? 1 : 0);
        CommandOptionCache.SetValue(Prefix + "BuildingArea.SquareMeters", units.ToSquareMeters(BuildingArea));
        CommandOptionCache.SetValue(Prefix + "SurfaceArea.SquareMeters", units.ToSquareMeters(SurfaceArea));
        CommandOptionCache.SetValue(Prefix + nameof(GreenThreshold), GreenThreshold);
        CommandOptionCache.SetValue(Prefix + nameof(PavingSaturation), PavingSaturation);
        CommandOptionCache.SetValue(Prefix + nameof(GroundSlope), GroundSlope);
        CommandOptionCache.SetValue(Prefix + nameof(RoofFaces), RoofFaces);
    }

    public ScanTerrainPipeline.Options Pipeline(ModelUnitContext units, CancellationToken token,
        Action<string> progress, Action<ScanTerrainPipeline.Result> preview) => new()
    {
        Ground = new()
        {
            CellSize = RasterCell, SmallestObject = units.FromMeters(2), LargestObject = LargestObject,
            GroundSlope = GroundSlope, InitialThreshold = InitialThreshold, MaximumThreshold = MaximumThreshold,
            Margin = ObjectMargin, MinimumObjectArea = units.FromSquareMeters(20), CancellationToken = token,
        },
        GroundSpacing = GroundSpacing, PlanTolerance = PlanTolerance, ReportProgress = progress, ReportPreview = preview,
        Buildings = Buildings ? new() { MinimumArea = BuildingArea, MinimumHeight = BuildingHeight,
            FootprintTolerance = PlanTolerance, RoofTolerance = RoofTolerance, MinimumRoofDetail = RoofDetail,
            MaximumRoofFaces = RoofFaces, CancellationToken = token } : null,
        Walls = Walls ? new() { MinimumHeight = WallHeight, MinimumLength = WallLength, PlanTolerance = PlanTolerance,
            VerticalTolerance = Math.Min(Tolerance, units.FromMeters(.1)), BorderMargin = GroundSpacing * 2,
            MaximumWidth = units.FromMeters(1), CancellationToken = token } : null,
        Areas = Surfaces ? new() { ExcessGreen = GreenThreshold, MaximumPavedSaturation = PavingSaturation,
            MaximumPavedRoughness = units.FromMeters(.1), MinimumArea = SurfaceArea,
            PlanTolerance = PlanTolerance, VerticalTolerance = Math.Min(Tolerance, units.FromMeters(.05)), CancellationToken = token } : null,
        Roads = RoadEdges ? new() { MinimumKerbHeight = KerbMin, MaximumKerbHeight = KerbMax, ProfileReach = units.FromMeters(1),
            MaximumKerbWidth = units.FromMeters(.6), MinimumLength = units.FromMeters(2), PlanTolerance = RoadPlanTolerance,
            VerticalTolerance = units.FromMeters(.015), RoadVerticalTolerance = Math.Min(Tolerance, RoadHeightTolerance), CancellationToken = token } : null,
    };
}
