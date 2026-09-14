namespace MoleHill.Rhino.Services;

/// <summary>Which parts of a terrain report to assemble. The drawn table lets a user leave sections
/// out; the CSV export takes them all.</summary>
[Flags]
internal enum TerrainReportSections
{
    None = 0,
    Overview = 1,
    Zones = 2,
    Earthworks = 4,
    Ponding = 8,
    Catchments = 16,
    All = Overview | Zones | Earthworks | Ponding | Catchments
}
