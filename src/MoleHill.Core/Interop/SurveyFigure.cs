namespace MoleHill.Core.Interop;

/// <summary>
/// One run of coded points that becomes a single piece of linework.
///
/// Holds indices into the <see cref="SurveyPointFile.Points"/> list rather than copies of the points, so
/// a figure cannot disagree with the file it came from, and geometry is built once at the host boundary.
///
/// <see cref="ArcFlags"/> is parallel to <see cref="PointIndices"/> — the flat-parallel form the rest of
/// the pipeline uses. Core records <i>which</i> points were flagged as lying on an arc and stops there:
/// fitting the arc needs Rhino's geometry, and duplicating a fitter in Core would give two answers.
/// </summary>
public sealed class SurveyFigure
{
    public SurveyFigure(
        string code,
        FieldCodeRole role,
        string layer,
        IReadOnlyList<int> pointIndices,
        IReadOnlyList<bool> arcFlags,
        bool isClosed,
        int? figureNumber)
    {
        Code = code;
        Role = role;
        Layer = layer;
        PointIndices = pointIndices;
        ArcFlags = arcFlags;
        IsClosed = isClosed;
        FigureNumber = figureNumber;
    }

    public string Code { get; }

    public FieldCodeRole Role { get; }

    public string Layer { get; }

    /// <summary>Indices into the source file's point list, in the order the points were surveyed.</summary>
    public IReadOnlyList<int> PointIndices { get; }

    /// <summary>Per-point arc flags, same length as <see cref="PointIndices"/>.</summary>
    public IReadOnlyList<bool> ArcFlags { get; }

    public bool IsClosed { get; }

    /// <summary>The figure number that separated this run from others of the same code, when it had one.</summary>
    public int? FigureNumber { get; }

    public int PointCount => PointIndices.Count;
}
