using MoleHill.Core.Interop;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class FieldCodeTableStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "MoleHillFieldCodes", Guid.NewGuid().ToString("N"));

    public FieldCodeTableStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test over.
        }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void WriteThenRead_DefaultTable_RoundTripsEveryField()
    {
        string path = PathFor("round-trip.json");
        FieldCodeTable original = FieldCodeTable.CreateDefault();

        FieldCodeTableStore.WriteTo(path, original);
        Assert.True(FieldCodeTableStore.TryRead(path, out FieldCodeTable? loaded));

        Assert.NotNull(loaded);
        Assert.Equal(original.Rules.Count, loaded!.Rules.Count);
        Assert.Equal(original.StartTokens, loaded.StartTokens);
        Assert.Equal(original.EndTokens, loaded.EndTokens);
        Assert.Equal(original.ArcTokens, loaded.ArcTokens);
        Assert.Equal(original.CloseTokens, loaded.CloseTokens);
        Assert.Equal(original.ContinuationSuffix, loaded.ContinuationSuffix);
        Assert.Equal(original.UnmatchedLayer, loaded.UnmatchedLayer);
    }

    [Fact]
    public void WriteThenRead_RoleAndClosedFlag_SurviveTheRoundTrip()
    {
        string path = PathFor("roles.json");
        var table = new FieldCodeTable
        {
            Rules =
            {
                new FieldCodeRule { Code = "BDY", Role = FieldCodeRole.Boundary, ClosedByDefault = true },
                new FieldCodeRule { Code = "TREE", Role = FieldCodeRole.Ignore }
            }
        };

        FieldCodeTableStore.WriteTo(path, table);
        Assert.True(FieldCodeTableStore.TryRead(path, out FieldCodeTable? loaded));

        Assert.Equal(FieldCodeRole.Boundary, loaded!.Find("BDY")!.Role);
        Assert.True(loaded.Find("BDY")!.ClosedByDefault);
        Assert.Equal(FieldCodeRole.Ignore, loaded.Find("TREE")!.Role);
    }

    [Fact]
    public void Read_RoleWrittenAsAName_IsUnderstood()
    {
        // The file is hand-editable, so roles are written as names rather than integers.
        string path = PathFor("named-role.json");
        File.WriteAllText(path, """{"Rules":[{"Code":"EP","Role":"Contour"}]}""");

        Assert.True(FieldCodeTableStore.TryRead(path, out FieldCodeTable? loaded));

        Assert.Equal(FieldCodeRole.Contour, loaded!.Find("EP")!.Role);
    }

    [Fact]
    public void Read_CorruptFile_ReportsFailureRatherThanThrowing()
    {
        string path = PathFor("corrupt.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.False(FieldCodeTableStore.TryRead(path, out FieldCodeTable? loaded));
        Assert.Null(loaded);
    }

    [Fact]
    public void LoadFrom_CorruptFile_ReturnsDefaultsWarnsAndLeavesTheFileUntouched()
    {
        // A read failure (a Dropbox lock, a truncated write) used to overwrite the user's table.
        string path = PathFor("field-codes.json");
        const string original = "{ this is not json";
        File.WriteAllText(path, original);

        FieldCodeTable loaded = FieldCodeTableStore.LoadFrom(path, out string? warning);

        Assert.Equal(FieldCodeTable.CreateDefault().Rules.Count, loaded.Rules.Count);
        Assert.NotNull(warning);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "field-codes.json.bak-*"));
    }

    [Fact]
    public void LoadFrom_LockedFile_ReturnsDefaultsWithoutThrowing()
    {
        string path = PathFor("field-codes.json");
        FieldCodeTableStore.WriteTo(path, FieldCodeTable.CreateDefault());

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            FieldCodeTable loaded = FieldCodeTableStore.LoadFrom(path, out string? warning);

            Assert.NotEmpty(loaded.Rules);
            Assert.NotNull(warning);
        }

        Assert.True(FieldCodeTableStore.TryRead(path, out _));
    }

    [Fact]
    public void LoadFrom_MissingFile_WritesTheDefaultsWithoutWarning()
    {
        string path = PathFor("field-codes.json");

        FieldCodeTable loaded = FieldCodeTableStore.LoadFrom(path, out string? warning);

        Assert.Null(warning);
        Assert.NotEmpty(loaded.Rules);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveTo_OverAnUnreadableFile_BacksItUpFirst()
    {
        string path = PathFor("field-codes.json");
        const string original = "{ this is not json";
        File.WriteAllText(path, original);

        FieldCodeTableStore.SaveTo(path, FieldCodeTable.CreateDefault());

        string backup = Assert.Single(Directory.GetFiles(_directory, "field-codes.json.bak-*"));
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.True(FieldCodeTableStore.TryRead(path, out _));
    }

    [Fact]
    public void SaveTo_OverAReadableFile_MakesNoBackup()
    {
        string path = PathFor("field-codes.json");
        FieldCodeTableStore.WriteTo(path, FieldCodeTable.CreateDefault());

        FieldCodeTableStore.SaveTo(path, FieldCodeTable.CreateDefault());

        Assert.Empty(Directory.GetFiles(_directory, "field-codes.json.bak-*"));
    }

    [Fact]
    public void Read_RoleNameFromANewerBuild_SkipsOnlyThatRuleAndWarns()
    {
        string path = PathFor("newer.json");
        File.WriteAllText(path, """{"Rules":[{"Code":"EP","Role":"Breakline"},{"Code":"WALL","Role":"RetainingWall"}]}""");

        Assert.True(FieldCodeTableStore.TryRead(path, out FieldCodeTable? loaded, out string? warning));

        Assert.NotNull(loaded!.Find("EP"));
        Assert.Null(loaded.Find("WALL"));
        Assert.Contains("WALL", warning);
    }

    [Fact]
    public void Read_MissingFile_ReportsFailureRatherThanThrowing()
    {
        Assert.False(FieldCodeTableStore.TryRead(PathFor("absent.json"), out _));
    }

    [Fact]
    public void Normalize_DuplicateCodes_KeepsOnlyTheFirst()
    {
        // Two rules for one code would make the winner depend on invisible list order.
        var table = new FieldCodeTable
        {
            Rules =
            {
                new FieldCodeRule { Code = "EP", Layer = "First" },
                new FieldCodeRule { Code = "ep", Layer = "Second" }
            }
        };

        FieldCodeTable normalized = FieldCodeTableStore.Normalize(table);

        Assert.Single(normalized.Rules);
        Assert.Equal("First", normalized.Rules[0].Layer);
    }

    [Fact]
    public void FindDuplicateCodes_SameCodeDifferentCaseAndSpacing_IsReportedOnce()
    {
        var rules = new[]
        {
            new FieldCodeRule { Code = "EP" },
            new FieldCodeRule { Code = " ep " },
            new FieldCodeRule { Code = "Ep" },
            new FieldCodeRule { Code = "TC" },
            new FieldCodeRule { Code = "  " }
        };

        Assert.Equal(new[] { "EP" }, FieldCodeTableStore.FindDuplicateCodes(rules));
    }

    [Fact]
    public void FindDuplicateCodes_DistinctCodes_ReportsNothing()
    {
        Assert.Empty(FieldCodeTableStore.FindDuplicateCodes(FieldCodeTable.CreateDefault().Rules));
    }

    [Fact]
    public void Normalize_BlankCodeRule_IsDropped()
    {
        var table = new FieldCodeTable
        {
            Rules = { new FieldCodeRule { Code = "  " }, new FieldCodeRule { Code = "EP" } }
        };

        Assert.Single(FieldCodeTableStore.Normalize(table).Rules);
    }

    [Fact]
    public void Normalize_CodesAndTokens_AreUppercasedAndTrimmed()
    {
        var table = new FieldCodeTable
        {
            Rules = { new FieldCodeRule { Code = "  ep " } },
            StartTokens = { " st ", "ST", "" }
        };

        FieldCodeTable normalized = FieldCodeTableStore.Normalize(table);

        Assert.Equal("EP", normalized.Rules[0].Code);
        Assert.Equal(new[] { "ST" }, normalized.StartTokens);
    }

    [Fact]
    public void Normalize_BlankUnmatchedLayer_FallsBackToTheDefault()
    {
        var table = new FieldCodeTable { UnmatchedLayer = "   " };

        Assert.Equal(FieldCodeTable.DefaultUnmatchedLayer, FieldCodeTableStore.Normalize(table).UnmatchedLayer);
    }

    [Fact]
    public void Normalize_LegacyTerrainInputLayers_AreMadeRelativeToTheSurvey()
    {
        var table = new FieldCodeTable
        {
            UnmatchedLayer = "MoleHill::Inputs::Unmatched Codes",
            Rules =
            {
                new FieldCodeRule { Code = "EP", Layer = "MoleHill::Inputs::Breaklines" },
                new FieldCodeRule { Code = "TC", Layer = "Survey::Top of Kerb" }
            }
        };

        FieldCodeTable normalized = FieldCodeTableStore.Normalize(table);

        Assert.Equal("Unmatched Codes", normalized.UnmatchedLayer);
        Assert.Equal("Breaklines", normalized.Rules[0].Layer);
        Assert.Equal("Survey::Top of Kerb", normalized.Rules[1].Layer);
    }

    [Fact]
    public void Normalize_UnknownRoleValue_FallsBackToBreakline()
    {
        var table = new FieldCodeTable { Rules = { new FieldCodeRule { Code = "EP", Role = (FieldCodeRole)99 } } };

        Assert.Equal(FieldCodeRole.Breakline, FieldCodeTableStore.Normalize(table).Rules[0].Role);
    }

    [Fact]
    public void Normalize_StampsTheCurrentVersion()
    {
        Assert.Equal(FieldCodeTable.CurrentVersion, FieldCodeTableStore.Normalize(new FieldCodeTable()).Version);
    }

    [Fact]
    public void WriteTo_DoesNotMutateTheCallersTable()
    {
        string path = PathFor("no-mutate.json");
        var table = new FieldCodeTable { Rules = { new FieldCodeRule { Code = "ep" } } };

        FieldCodeTableStore.WriteTo(path, table);

        Assert.Equal("ep", table.Rules[0].Code);
    }

    [Fact]
    public void ExportThenImport_CarriesTheTableBetweenMachines()
    {
        string path = PathFor("shared.json");
        FieldCodeTable table = FieldCodeTable.CreateDefault();
        table.Rules.Add(new FieldCodeRule { Code = "SW", Role = FieldCodeRole.Breakline, Layer = "Survey::Swale" });

        FieldCodeTableStore.Export(path, table);
        Assert.True(FieldCodeTableStore.Import(path, out FieldCodeTable? imported));

        Assert.Equal("Survey::Swale", imported!.Find("SW")!.Layer);
    }

    [Fact]
    public void GetStorePath_SitsBesideTheLayerTemplateFile()
    {
        string path = new FieldCodeTableStore().GetStorePath();

        Assert.EndsWith("field-codes.json", path);
        Assert.Equal(
            Path.GetDirectoryName(new LayerTemplateStore().GetStorePath()),
            Path.GetDirectoryName(path));
    }
}
