using Rhino.Geometry;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainBuildResult
{
    public Mesh? PrimaryMesh { get; set; }

    public List<GeneratedRhinoObject> ZoneObjects { get; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; } = new();

    public List<string> Diagnostics { get; } = new();

    public TerrainAnalysisSummary? Analysis { get; set; }
}
