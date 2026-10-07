using System.Reflection;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The exporter writes C# source. Source that does not compile, or that compiles but omits an argument,
/// is a case that cannot be replayed — and the omission is invisible until someone tries. These pin the
/// emitted call shape; <c>MOLEHILL_EMIT_CASE_DIR</c> additionally dumps the source so it can be compiled.
/// </summary>
public class TerrainCoreCaseTestExporterEmitTests
{
    private static string GenerateSource(TerrainCorePathCaseRecord record)
    {
        MethodInfo generate = typeof(TerrainCoreCaseTestExporter)
            .GetMethod("GenerateSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GenerateSource not found.");

        return (string)generate.Invoke(null, new object[] { "EmittedCaseTests", "EmittedCase", record })!;
    }

    private static TerrainCorePathCaseRecord CreateWallRailRecord() => new(
        StageName: "Retaining Wall Rails",
        Succeeded: true,
        ResultVertexCount: 36,
        ResultFaceCount: 55,
        Message: null,
        Vertices: new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 1.0, 0.0, 10.0, 2.0 },
        VertexCount: 3,
        Faces: new[] { 0, 1, 2 },
        FaceCount: 1,
        Paths: new[]
        {
            new PathGrader.PathDefinition(
                new[] { 0.0, 0.0, 10.0, 0.0 },
                new[] { 4.0, 4.0 },
                2,
                0.0,
                45.0,
                12.0,
                33.0,
                null,
                null,
                false,
                41.0,
                43.0,
                47.0,
                53.0,
                new[] { 0.0, 1.0, 0.0, 1.0 })
        },
        HardConstraints: Array.Empty<ConstraintPolyline>(),
        ModelTolerance: 0.01,
        PreferSplitKeep: true);

    [Fact]
    public void GeneratedSource_CarriesOutwardNormalsAndTierArguments()
    {
        string source = GenerateSource(CreateWallRailRecord());

        // The one-sidedness of the rail, and the two arguments that pick the grading tier.
        Assert.Contains("ModelTolerance = 0.01", source);
        Assert.Contains("PreferSplitKeep = true", source);
        Assert.Contains("41", source);
        Assert.Contains("53", source);

        // Emitted as a single PathDefinition construction, with hardConstraints still threaded.
        Assert.Contains("new PathGrader.PathDefinition(", source);
        Assert.Contains("HardConstraints = hardConstraints,", source);
        Assert.Contains("string? errorMessage = outcome.ErrorMessage;", source);
    }

    /// <summary>
    /// An inline array of whole numbers emitted as <c>new[]</c> infers <c>int[]</c>, and the generated
    /// case then fails to compile with CS1503 — found by compiling a dumped case, not by reading it. A
    /// wall rail's outward normals are exactly 0 and 1, so this hit essentially every wall case.
    /// </summary>
    [Fact]
    public void GeneratedSource_TypesInlineArraysAsDoubleNotInferred()
    {
        string source = GenerateSource(CreateWallRailRecord());

        // "var paths = new[]" is legitimately inferred (PathDefinition[]); the inline numeric arrays
        // nested inside it are the ones that must be typed.
        string[] inferredNumericArrayLines = source
            .Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.Trim() == "new[]")
            .ToArray();

        Assert.Empty(inferredNumericArrayLines);
        Assert.Contains("new double[]", source);
    }

    [Fact]
    public void GeneratedSource_HasBalancedDelimiters()
    {
        string source = GenerateSource(CreateWallRailRecord());

        Assert.Equal(source.Count(c => c == '('), source.Count(c => c == ')'));
        Assert.Equal(source.Count(c => c == '{'), source.Count(c => c == '}'));
        Assert.Equal(source.Count(c => c == '['), source.Count(c => c == ']'));
    }

    /// <summary>
    /// Writes the generated case to <c>MOLEHILL_EMIT_CASE_DIR</c> so a build can prove it compiles.
    /// Off by default: this writes a file, and an env-gated probe must never run under the ordinary
    /// suite where parallel tests would contend on it.
    /// </summary>
    [Fact]
    public void GeneratedSource_CanBeDumpedForCompilation()
    {
        string? directory = Environment.GetEnvironmentVariable("MOLEHILL_EMIT_CASE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "EmittedCase.cs"), GenerateSource(CreateWallRailRecord()));
    }
}
