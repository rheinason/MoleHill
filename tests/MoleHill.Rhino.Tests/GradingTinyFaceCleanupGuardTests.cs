using MoleHill.Core.Tests;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Grading stages finish their mesh with <c>FinalizeGradingMesh</c>, never <c>CleanTinyFaces</c>. The cleanup
/// judges a face by its plan area, so every steep face in the terrain reads as tiny, and its hole guard turns
/// itself off whenever the incoming border already touches itself. On a real case (In-Situ Stair after a
/// Retaining Wall, 2026-10-26) it then deleted all 558 such faces: both rails of the wall tore open along
/// their whole length, and small holes opened across the site, far from the stair.
/// </summary>
public class GradingTinyFaceCleanupGuardTests
{
    private static readonly string[] GradingStages =
    {
        "GradeLineStage.cs",
        "GradePadStage.cs",
        "GradePathStage.cs",
        "InSituStairStage.cs",
    };

    [Fact]
    public void GradingStages_NeverCallCleanTinyFaces()
    {
        string stages = Path.Combine(RepositoryPaths.FindRoot(), "src", "MoleHill.Rhino", "Services", "Build", "Stages");
        foreach (string stage in GradingStages)
        {
            string source = File.ReadAllText(Path.Combine(stages, stage));
            Assert.False(source.Contains("CleanTinyFaces(", StringComparison.Ordinal), $"{stage} calls CleanTinyFaces; grading output must not introduce holes.");
        }
    }
}
