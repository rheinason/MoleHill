namespace MoleHill.Rhino.Model;

/// <summary>
/// One persisted colour-ramp stop.
///
/// The Core <c>SlopeAnalyzer.ColorStop</c> is deliberately not serialized directly: it is a computation
/// type whose shape belongs to the analysis engine, and pinning the document's JSON to it would mean any
/// change there is a document-format change. This is the document's own contract — a position and an ARGB
/// int, matching how every other colour in a terrain definition is stored.
/// </summary>
public sealed class AnalysisColorStopState
{
    /// <summary>Normalized position along the ramp, 0..1.</summary>
    public double Position { get; set; }

    public int ColorArgb { get; set; }
}
