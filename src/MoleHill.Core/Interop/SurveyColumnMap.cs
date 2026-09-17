namespace MoleHill.Core.Interop;

/// <summary>
/// Which column of a survey point file holds which field.
///
/// <para><b>The trap this type exists to make explicit: PNEZD is not XYZ.</b> Its columns are Point,
/// <i>Northing</i>, <i>Easting</i>, Z, Description — so the first two coordinate columns are the
/// opposite way round from the obvious reading, and northing is Y while easting is X. A file read with
/// the two swapped parses perfectly and produces a terrain transposed about the 45° line: plausible,
/// entirely wrong, and undetectable by any later stage. Every field below is therefore named for what
/// it <i>means</i> (<see cref="EastingColumn"/>, <see cref="NorthingColumn"/>) rather than for an axis,
/// and the preset that produced it is remembered in <see cref="PresetName"/> so a UI can show the user
/// what was assumed.</para>
///
/// Indices are 0-based positions in the split row. Presets cover the common deliveries; the indices stay
/// settable because real files carry extra columns the presets do not name.
/// </summary>
public sealed class SurveyColumnMap
{
    /// <summary>Column holding the point number, or null when the format carries none.</summary>
    public int? NumberColumn { get; set; }

    /// <summary>Column holding the easting. Resolves to <see cref="SurveyPoint.X"/>.</summary>
    public int EastingColumn { get; set; }

    /// <summary>Column holding the northing. Resolves to <see cref="SurveyPoint.Y"/>.</summary>
    public int NorthingColumn { get; set; }

    /// <summary>Column holding the elevation.</summary>
    public int ElevationColumn { get; set; }

    /// <summary>Column where the description starts, or null when the format carries none.</summary>
    public int? DescriptionColumn { get; set; }

    /// <summary>
    /// Whether the description runs to the end of the row rather than occupying one column.
    ///
    /// On by default for every preset, because field descriptions carry unquoted commas constantly
    /// ("EP, START OF KERB"). Reading only the first column would silently truncate the code's markers,
    /// which is a data-loss bug that looks like a convention the crew did not use.
    /// </summary>
    public bool DescriptionSpansRemainingColumns { get; set; } = true;

    /// <summary>Name of the preset this map came from, or "Custom" once the user edits an index.</summary>
    public string PresetName { get; set; } = CustomPresetName;

    public const string CustomPresetName = "Custom";

    /// <summary>The lowest row length this map can read. A row shorter than this cannot be mapped.</summary>
    public int RequiredColumnCount
    {
        get
        {
            int required = Math.Max(EastingColumn, Math.Max(NorthingColumn, ElevationColumn));
            if (NumberColumn is { } number)
                required = Math.Max(required, number);
            if (DescriptionColumn is { } description)
                required = Math.Max(required, description);
            return required + 1;
        }
    }

    /// <summary>
    /// The named deliveries, in the order a picker should offer them. PNEZD first because it is what a
    /// surveyor sends unless told otherwise.
    /// </summary>
    public static IReadOnlyList<string> PresetNames { get; } = new[] { "PNEZD", "PENZD", "ENZ", "NEZ", "XYZ" };

    /// <summary>Returns a fresh map for a named preset, or null when the name is not one.</summary>
    public static SurveyColumnMap? FromPreset(string presetName) => presetName switch
    {
        // Point, Northing, Easting, Z, Description — note N before E.
        "PNEZD" => new SurveyColumnMap
        {
            NumberColumn = 0,
            NorthingColumn = 1,
            EastingColumn = 2,
            ElevationColumn = 3,
            DescriptionColumn = 4,
            PresetName = "PNEZD"
        },
        // Point, Easting, Northing, Z, Description.
        "PENZD" => new SurveyColumnMap
        {
            NumberColumn = 0,
            EastingColumn = 1,
            NorthingColumn = 2,
            ElevationColumn = 3,
            DescriptionColumn = 4,
            PresetName = "PENZD"
        },
        "ENZ" => new SurveyColumnMap
        {
            EastingColumn = 0,
            NorthingColumn = 1,
            ElevationColumn = 2,
            DescriptionColumn = 3,
            PresetName = "ENZ"
        },
        "NEZ" => new SurveyColumnMap
        {
            NorthingColumn = 0,
            EastingColumn = 1,
            ElevationColumn = 2,
            DescriptionColumn = 3,
            PresetName = "NEZ"
        },
        // Named separately from ENZ although the indices agree: a file the user thinks of as XYZ is not
        // one they should have to recognise as "easting, northing".
        "XYZ" => new SurveyColumnMap
        {
            EastingColumn = 0,
            NorthingColumn = 1,
            ElevationColumn = 2,
            DescriptionColumn = 3,
            PresetName = "XYZ"
        },
        _ => null
    };

    public SurveyColumnMap Clone() => new()
    {
        NumberColumn = NumberColumn,
        EastingColumn = EastingColumn,
        NorthingColumn = NorthingColumn,
        ElevationColumn = ElevationColumn,
        DescriptionColumn = DescriptionColumn,
        DescriptionSpansRemainingColumns = DescriptionSpansRemainingColumns,
        PresetName = PresetName
    };
}
