using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

/// <summary>
/// The terrain's measured quantities, drawn into the model as a table — the equivalent of Civil 3D's
/// inserted cut/fill summary.
///
/// <para>It is an annotation rather than a one-shot command output because it <em>describes</em> the
/// terrain, and because that is what makes it live: the table is rebuilt with the terrain, so a figure
/// on the drawing cannot be left over from a design two revisions ago. A command that stamped text once
/// would put the burden of noticing staleness on the reader, which is the entire thing a live model is
/// supposed to remove.</para>
///
/// <para>It draws what the rest of the build measured; it measures nothing itself. Every figure comes
/// through <c>TerrainReportBuilder</c>, the same assembly the CSV export writes, so the drawn table and
/// the exported file cannot disagree.</para>
/// </summary>
public sealed class ReportTableAnnotationDefinition : AnnotationDefinition
{
    public ReportTableAnnotationDefinition()
    {
        Label = "Report Table";
    }

    /// <summary>The terrain name, generated-on stamp, surface area and elevation range.</summary>
    public bool IncludeOverview { get; set; } = true;

    /// <summary>The per-zone schedule with its totals row — the reason this annotation exists.</summary>
    public bool IncludeZones { get; set; } = true;

    /// <summary>Cut, fill and net from each Earthworks analysis.</summary>
    public bool IncludeEarthworks { get; set; } = true;

    /// <summary>Pond count, impounded volume, depth and water area from each Ponding analysis.</summary>
    public bool IncludePonding { get; set; } = true;

    /// <summary>Basin and closed-depression counts from each Catchments analysis.</summary>
    public bool IncludeCatchments { get; set; } = true;

    /// <summary>Level area checked, area over the limit and steepest slope from each Gradient Compliance analysis.</summary>
    public bool IncludeGradientCompliance { get; set; } = true;

    /// <summary>
    /// Unit the drawn slope columns are written in. Stored with the terrain, because it is part of the
    /// drawing — the same reason every other annotation carries its own unit rather than following the
    /// per-user input preference. The CSV export does follow that preference: it is a thing you read,
    /// not a thing the document says.
    /// </summary>
    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    /// <summary>Rules under the headings and around the table. Off draws text only.</summary>
    public bool ShowGridLines { get; set; } = true;

    /// <summary>
    /// Gap between columns, as a multiple of the text height rather than an absolute length, so the table
    /// stays proportioned when the annotation style is rescaled — which is the whole point of following
    /// the style in the first place.
    /// </summary>
    [UnitFree("A multiple of the text height, not an absolute length.")]
    public double ColumnGap { get; set; } = 1.5;

    /// <summary>Line spacing as a multiple of the text height.</summary>
    [UnitFree("A multiple of the text height, not an absolute length.")]
    public double RowSpacing { get; set; } = 1.8;

    /// <summary>Absolute text height, used only when <see cref="AnnotationDefinition.FollowsAnnotationStyle"/>
    /// is false — the same arrangement the section annotations use.</summary>
    [ModelLength]
    public double TextHeight { get; set; } = 1.0;

    public int? ColorArgb { get; set; }

    [ModelLength]
    public double InsertionOriginX { get; set; }

    [ModelLength]
    public double InsertionOriginY { get; set; }

    [ModelLength]
    public double InsertionOriginZ { get; set; }

    /// <summary>False places the table beside the terrain's bounding box, so a freshly added card draws
    /// something without asking for a pick first.</summary>
    public bool HasInsertionPlane { get; set; }
}
