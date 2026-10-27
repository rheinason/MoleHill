using MoleHill.Core.Tests;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Grading stages finish their mesh with <c>FinalizeGradingMesh</c>, never <c>CleanTinyFaces</c>: grading output
/// must not lose a face. When the In-Situ Stair still ran the cleanup (a copied case, 2026-10-26), it judged faces
/// by plan area with its hole guard off, deleted all 558 steep faces of a Retaining Wall and tore both rails open
/// along their whole length, with small holes across the site. The cleanup itself is now border-only and judged
/// in 3D (<c>TerrainBuildServiceTinyFaceCleanupTests</c>); grading still has no business deleting faces.
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
