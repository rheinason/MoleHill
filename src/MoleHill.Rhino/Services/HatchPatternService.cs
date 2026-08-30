using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The boundary between MoleHill's generated fills and Rhino's hatch-pattern table, mirroring
/// <see cref="AnnotationStyleService"/>.
///
/// Filled drawing regions are emitted as real <see cref="Rhino.Geometry.Hatch"/> objects rather than
/// transparent shaded meshes: a mesh is a rendering artefact that does not print sensibly and ignores
/// <see cref="RhinoDoc.ModelSpaceHatchScale"/>, whereas a hatch is an ordinary drawing element whose
/// pattern, scale, and print appearance the user controls with Rhino's own tools.
///
/// Pattern indices are document-scoped, so they must be resolved on the document thread and carried into
/// the background build on the snapshot (the same pattern as block-definition bounds).
/// </summary>
internal static class HatchPatternService
{
    public const string SolidPatternName = "Solid";

    /// <summary>
    /// Cut and fill both default to a solid tint, told apart by their layer colour rather than by pattern.
    ///
    /// Line hatches were the earlier default and they lose at the scale these are read at: at drawing
    /// zoom the strokes alias into a grey wash, and at section scale a wedge a few millimetres deep shows
    /// one or two strokes and reads as empty. A solid tint carries its colour at any size, which is what
    /// makes cut and fill legible at a glance. Both are still overridable per analysis, and the pattern
    /// itself is edited in Rhino's hatch pattern table.
    /// </summary>
    public const string DefaultCutPatternName = SolidPatternName;

    public const string DefaultFillPatternName = SolidPatternName;

    /// <summary>
    /// The built-in patterns MoleHill will create on demand. Rhino ships these as
    /// <see cref="HatchPattern.Defaults"/> but a given document may not contain them yet.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<HatchPattern>> BuiltInPatterns =
        new Dictionary<string, Func<HatchPattern>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Solid"] = () => HatchPattern.Defaults.Solid,
            ["Hatch1"] = () => HatchPattern.Defaults.Hatch1,
            ["Hatch2"] = () => HatchPattern.Defaults.Hatch2,
            ["Hatch3"] = () => HatchPattern.Defaults.Hatch3,
            ["Dash"] = () => HatchPattern.Defaults.Dash,
            ["Grid"] = () => HatchPattern.Defaults.Grid,
            ["Grid60"] = () => HatchPattern.Defaults.Grid60,
            ["Plus"] = () => HatchPattern.Defaults.Plus,
            ["Squares"] = () => HatchPattern.Defaults.Squares
        };

    public static IReadOnlyCollection<string> BuiltInPatternNames => (IReadOnlyCollection<string>)BuiltInPatterns.Keys;

    public static string ResolvePatternName(string? patternName, string fallback) =>
        string.IsNullOrWhiteSpace(patternName) ? fallback : patternName.Trim();

    /// <summary>
    /// Returns the index of the named hatch pattern, adding a built-in definition when the document does
    /// not already have one. A user-authored pattern of the same name is reused untouched. Returns -1 when
    /// the name is neither present nor a known built-in, which callers treat as "no hatch".
    /// </summary>
    public static int EnsurePattern(RhinoDoc doc, string? patternName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string name = ResolvePatternName(patternName, SolidPatternName);

        HatchPattern? existing = doc.HatchPatterns.FindName(name);
        if (existing != null)
            return existing.Index;

        if (!BuiltInPatterns.TryGetValue(name, out Func<HatchPattern>? factory))
            return -1;

        HatchPattern pattern = factory();
        pattern.Name = name;
        return doc.HatchPatterns.Add(pattern);
    }

    /// <summary>
    /// Resolves every hatch pattern the terrain's analyses reference into document indices, on the document
    /// thread, for the background build to use.
    /// </summary>
    public static HatchPatternSnapshot Capture(RhinoDoc doc, IEnumerable<string?> patternNames)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(patternNames);

        var entries = new Dictionary<string, HatchPatternEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string? requested in patternNames.Append(SolidPatternName))
        {
            string name = ResolvePatternName(requested, SolidPatternName);
            if (entries.ContainsKey(name))
                continue;

            int index = EnsurePattern(doc, name);
            entries[name] = new HatchPatternEntry(index, GetNativeLineOffset(doc, index));
        }

        return new HatchPatternSnapshot(entries);
    }

    /// <summary>
    /// The pattern's own line spacing in model units at scale 1. Needed because a pattern scale is
    /// meaningless on its own: Rhino's Hatch1 spaces lines 0.125 units apart, so scale 1 on a 120 m
    /// section draws ~64 lines per metre and reads as solid black. Returns 0 for solid/unresolvable
    /// patterns, which callers treat as "not derivable".
    /// </summary>
    private static double GetNativeLineOffset(RhinoDoc doc, int patternIndex)
    {
        if (patternIndex < 0 || patternIndex >= doc.HatchPatterns.Count)
            return 0.0;

        HatchPattern pattern = doc.HatchPatterns[patternIndex];
        double smallest = 0.0;
        foreach (HatchLine line in pattern.HatchLines)
        {
            double offset = line.Offset.Length;
            if (offset <= RhinoMath.ZeroTolerance)
                continue;
            if (smallest <= 0.0 || offset < smallest)
                smallest = offset;
        }

        return smallest;
    }
}

/// <summary>Index and native line spacing of one captured pattern.</summary>
internal readonly record struct HatchPatternEntry(int Index, double NativeLineOffset);

/// <summary>Immutable document-thread capture of hatch-pattern indices. See
/// <see cref="HatchPatternService.Capture"/>.</summary>
internal sealed class HatchPatternSnapshot
{
    /// <summary>Hatch lines this far apart on paper read as a texture rather than as a smear or an empty
    /// region. Expressed as a fraction of annotation text height so a hatch keeps its relationship to the
    /// labels beside it at any drawing scale.</summary>
    private const double SpacingPerTextHeight = 0.8;

    private readonly IReadOnlyDictionary<string, HatchPatternEntry> _entries;

    public HatchPatternSnapshot()
        : this(new Dictionary<string, HatchPatternEntry>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public HatchPatternSnapshot(IReadOnlyDictionary<string, HatchPatternEntry> entries)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
    }

    /// <summary>
    /// Document index for a pattern name, falling back to Solid so a requested fill degrades rather than
    /// disappearing.
    /// </summary>
    public int ResolveIndex(string? patternName, string fallbackPatternName)
    {
        if (TryGetEntry(patternName, fallbackPatternName, out HatchPatternEntry entry))
            return entry.Index;

        return 0;
    }

    /// <summary>
    /// Pattern scale to draw this hatch at. A stored <paramref name="storedScale"/> above zero is an
    /// explicit user choice and is used as-is. Zero means "derive": pick the scale whose resulting line
    /// spacing is proportional to the annotation text height, so the fill reads consistently at whatever
    /// scale the drawing is set up for instead of depending on the pattern's arbitrary native spacing.
    /// </summary>
    public double ResolveScale(
        string? patternName,
        string fallbackPatternName,
        double storedScale,
        double annotationTextHeight)
    {
        if (storedScale > 0.0)
            return storedScale;

        if (!TryGetEntry(patternName, fallbackPatternName, out HatchPatternEntry entry) ||
            entry.NativeLineOffset <= 0.0 ||
            annotationTextHeight <= 0.0)
        {
            return 1.0;
        }

        double targetSpacing = annotationTextHeight * SpacingPerTextHeight;
        double scale = targetSpacing / entry.NativeLineOffset;
        return scale > 0.0 ? scale : 1.0;
    }

    private bool TryGetEntry(string? patternName, string fallbackPatternName, out HatchPatternEntry entry)
    {
        string name = HatchPatternService.ResolvePatternName(patternName, fallbackPatternName);
        if (_entries.TryGetValue(name, out entry) && entry.Index >= 0)
            return true;

        return _entries.TryGetValue(HatchPatternService.SolidPatternName, out entry) && entry.Index >= 0;
    }
}
