using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The single boundary between MoleHill's generated annotation and Rhino's dimension-style system.
///
/// MoleHill does not own annotation styling: sizes, fonts, arrowheads, and masks live on a normal Rhino
/// dimension style that the user edits with Rhino's own Annotation Styles editor. Generated text binds to
/// that style rather than carrying a hardcoded <see cref="global::Rhino.Geometry.AnnotationBase.TextHeight"/>, and
/// marker block instances are scaled from the same style's effective text height so symbols and labels stay
/// visually consistent.
///
/// "Effective" height is <c>TextHeight * DimensionScale</c>. Every dimension-style length is multiplied by
/// <c>DimensionScale</c>, which is also what Rhino's annotation scaling adjusts, so deriving block scale
/// from it keeps blocks in step with text whenever the style is rescaled.
/// </summary>
internal static class AnnotationStyleService
{
    public const string DefaultStyleName = "MoleHill Annotation";

    /// <summary>Marker blocks are authored with their internal text at height 1.0
    /// (see <see cref="GeneratedBlockCatalog"/>), so an instance scale of N yields text of height N.</summary>
    public const double AuthoredBlockTextHeight = 1.0;

    public static string ResolveStyleName(string? styleName) =>
        string.IsNullOrWhiteSpace(styleName) ? DefaultStyleName : styleName.Trim();

    /// <summary>
    /// Returns the index of the named dimension style, creating it from the document's current style if it
    /// does not exist. Returns the document's current style index if creation fails, so callers always get a
    /// usable style rather than an unassigned one.
    /// </summary>
    public static int EnsureStyle(RhinoDoc doc, string? styleName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string name = ResolveStyleName(styleName);

        DimensionStyle? existing = doc.DimStyles.FindName(name);
        if (existing != null)
            return existing.Index;

        int index = doc.DimStyles.Add(name, false);
        if (index < 0)
            return doc.DimStyles.CurrentIndex;

        return index;
    }

    /// <summary>Looks up the named style without creating it. Null when absent.</summary>
    public static DimensionStyle? FindStyle(RhinoDoc doc, string? styleName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.DimStyles.FindName(ResolveStyleName(styleName));
    }

    /// <summary>
    /// Captures the document-thread state the background build needs: the style id to stamp on generated
    /// text and the effective text height used to size marker blocks. The build runs off a snapshot with no
    /// document access, so this must be resolved on the main thread (as block definition bounds are).
    /// </summary>
    public static AnnotationStyleSnapshot Capture(RhinoDoc doc, string? styleName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string name = ResolveStyleName(styleName);

        // Create the style up front rather than at first bake, so the viewport preview and the baked
        // document objects are sized by the same style from the very first build. Idempotent: an existing
        // style of this name is reused untouched, and the user's edits to it are never overwritten.
        int index = EnsureStyle(doc, name);
        DimensionStyle? style = index >= 0 && index < doc.DimStyles.Count
            ? doc.DimStyles[index]
            : null;

        if (style == null)
            return new AnnotationStyleSnapshot { StyleName = name };

        return new AnnotationStyleSnapshot
        {
            StyleName = name,
            StyleId = style.Id,
            TextHeight = GetEffectiveTextHeight(style)
        };
    }

    /// <summary>
    /// The document's annotation styles a terrain can use, by name: its own, not ones referenced from a
    /// linked file or deleted. Sorted, with the MoleHill default first so it is always offered even before
    /// the first build creates it.
    /// </summary>
    public static IReadOnlyList<string> ListStyleNames(RhinoDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var names = doc.DimStyles
            .Where(style => style != null && !style.IsDeleted && !style.IsReference && !string.IsNullOrWhiteSpace(style.Name))
            .Select(style => style.Name)
            .Where(name => !string.Equals(name, DefaultStyleName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        names.Insert(0, DefaultStyleName);
        return names;
    }

    /// <summary>The model-space height of one line of text drawn in this style.</summary>
    public static double GetEffectiveTextHeight(DimensionStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return GetEffectiveTextHeight(style.TextHeight, style.DimensionScale);
    }

    /// <summary>
    /// The arithmetic behind <see cref="GetEffectiveTextHeight(DimensionStyle)"/>, split out so it is
    /// reachable without constructing a native Rhino <see cref="DimensionStyle"/>.
    /// </summary>
    public static double GetEffectiveTextHeight(double textHeight, double dimensionScale)
    {
        double height = textHeight * dimensionScale;
        return height > 0.0 ? height : AuthoredBlockTextHeight;
    }
}

/// <summary>Immutable document-thread capture of the terrain's annotation style. See
/// <see cref="AnnotationStyleService.Capture"/>.</summary>
internal sealed class AnnotationStyleSnapshot
{
    public required string StyleName { get; init; }

    public Guid StyleId { get; init; }

    /// <summary>Effective model-space text height; also the base scale for marker block instances.</summary>
    public double TextHeight { get; init; } = AnnotationStyleService.AuthoredBlockTextHeight;

    /// <summary>
    /// Instance scale for a marker block so its authored text matches this style's text height.
    /// <paramref name="relativeScale"/> is the per-analysis/per-marker Block Scale nudge (1.0 = match).
    /// </summary>
    public double GetBlockScale(double relativeScale)
    {
        double baseScale = TextHeight / AnnotationStyleService.AuthoredBlockTextHeight;
        double scale = baseScale * (relativeScale > 0.0 ? relativeScale : 1.0);
        return scale > 0.0 ? scale : 1.0;
    }

    /// <summary>Resolves a per-object text height: an explicit override when set, otherwise the style
    /// height. Legacy documents keep their stored absolute heights as overrides.</summary>
    public double ResolveTextHeight(double? explicitHeight) =>
        explicitHeight is > 0.0 ? explicitHeight.Value : TextHeight;
}
