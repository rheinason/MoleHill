using System.Globalization;
using MoleHill.Core.Displacement;
using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Displace: lifts and lowers the incoming ground by procedural noise or a greyscale image laid out in
/// world XY. The Blender Displace modifier is the model; the difference is that breaklines, walls and
/// Protect curves hold their height and the edit fades in from them.
/// </summary>
internal sealed class DisplaceModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "displace";
    public override Type DefinitionType => typeof(DisplaceModifierDefinition);
    public override string DisplayName => "Displace";
    public override string IconName => "ModDisplace";
    public override int SortOrder => 4;
    public override string Subtitle => "Raise and lower the ground by noise or an image";

    public override ModifierDefinition Create(UnitSystem unitSystem) => new DisplaceModifierDefinition
    {
        Height = ModelUnits.FromMeters(0.5, unitSystem),
        Feather = ModelUnits.FromMeters(2.0, unitSystem),
        NoiseSize = ModelUnits.FromMeters(10.0, unitSystem),
        TileSizeX = ModelUnits.FromMeters(20.0, unitSystem),
        TileSizeY = ModelUnits.FromMeters(20.0, unitSystem),
    };

    public override void RunBuildStage(ModifierBuildContext context) => DisplaceStage.Run(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var d = (DisplaceModifierDefinition)modifier;
        string region = d.Boundaries.HasReferences ? $"{CountSources(d.Boundaries)} boundaries" : "whole terrain";
        return DisplaceSources.IsImage(d)
            ? $"Image {DescribeFileName(d.ImagePath)} | {(d.Mapping.HasReferences ? "Tile from rectangle" : $"Tile {d.TileSizeX:G4} x {d.TileSizeY:G4}")} | Height {d.Height:G4} | {region}"
            : $"Noise | Size {d.NoiseSize:G4} | Height {d.Height:G4} | Seed {d.Seed} | {region}";
    }

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot)
    {
        var d = (DisplaceModifierDefinition)modifier;
        if (!(Math.Abs(d.Height) > 0.0))
            return "Set a Height to displace by.";
        if (snapshot != null && d.Boundaries.HasReferences && NoneResolve(snapshot, d.Boundaries))
            return "Its boundaries are no longer in the document. Pick new ones, or clear them to displace the whole terrain.";

        if (!DisplaceSources.IsImage(d))
            return d.NoiseSize > 0.0 ? null : "Set a Noise Size.";

        if (d.Mapping.HasReferences)
        {
            // Judged on the resolved curve; without a snapshot (tests) only the typed fallbacks are checked.
            if (snapshot != null && !DisplaceStage.TryResolveMappingRectangle(snapshot, d, out _, out string? mappingError))
                return mappingError;
        }
        else if (!(d.TileSizeX > 0.0) || !(d.TileSizeY > 0.0))
        {
            return "Set the Tile Size the image covers on the ground.";
        }

        // A missing or unreadable image would otherwise displace by nothing and look like a working card.
        return HeightmapImageLoader.TryLoad(d.ImagePath, out _, out _, out string? error) ? null : error;
    }

    /// <summary>
    /// The card's readout of a run: how finely the terrain samples the pattern, how far it moved, and a
    /// warning when the edges are too long for the pattern — the mesh is displaced only at its vertices, so a
    /// mesh coarser than half the pattern's detail cannot show it.
    /// </summary>
    internal static ModifierStageReport DescribeResult(
        TerrainDisplacer.Result result,
        IDisplacementSource source,
        Func<double, string> length,
        MappingRectangle? rectangle = null)
    {
        var rows = new List<ResultRow>();
        if (rectangle is { } r)
        {
            rows.Add(ResultRow.Of(
                "Mapping",
                $"rectangle {length(r.Width)} x {length(r.Height)}, turned {r.RotationDegrees.ToString("0.##", CultureInfo.InvariantCulture)} deg, " +
                $"bottom-left at {r.OriginX.ToString("0.###", CultureInfo.InvariantCulture)}, {r.OriginY.ToString("0.###", CultureInfo.InvariantCulture)}",
                "The image placement read from the Mapping rectangle, which overrides the typed Origin, Tile Width, Tile Height and Rotation."));
        }

        if (result.MovedVertexCount == 0)
        {
            rows.Add(ResultRow.Of(
                "Offset",
                "nothing moved",
                "No vertex is inside the boundaries, or every one inside is held (breaklines, walls, Protect, the border)."));
            return new ModifierStageReport(rows);
        }

        double median = result.MedianEdgeLength;
        double detail = source.DetailSize;
        string? warning = null;
        if (source is ImageDisplacementSource image)
        {
            rows.Add(ResultRow.Of(
                "Sampling",
                $"edges {length(median)} (median), resolves detail down to about {length(2.0 * median)}; pixels {length(detail)}",
                "Displace moves the terrain's vertices only, so the median edge length where it reaches is the resolution the image is read at: detail smaller than about two edges is lost."));
            warning = DescribeImageUndersampling(result, image, length);
        }
        else
        {
            string finest = source is NoiseDisplacementSource noise && noise.Octaves > 1
                ? $", finest octave {length(noise.FinestSize)}"
                : string.Empty;
            rows.Add(ResultRow.Of(
                "Sampling",
                $"edges {length(median)} (median), noise {length(detail)}{finest}",
                "Displace moves the terrain's vertices only, so the median edge length where it reaches is the resolution the noise is read at. Octaves finer than twice that edge length are lost."));
            if (median > 0.5 * detail)
            {
                warning =
                    $"The terrain here is sampled every {length(median)}, coarser than half the Noise Size ({length(0.5 * detail)}), " +
                    "so the noise is undersampled: its bumps are lost or alias into coarser ones. Add a Remesh below this card " +
                    $"with an Edge Length of {length(0.5 * detail)} or less, or raise the Noise Size.";
            }
        }

        rows.Add(ResultRow.Of(
            "Offset",
            $"{length(result.MinOffset)} to {length(result.MaxOffset)} over {result.MovedVertexCount.ToString("N0", CultureInfo.InvariantCulture)} vertices",
            "The lowest and highest height change this run made."));
        if (result.HeldVertexCount > 0)
        {
            rows.Add(ResultRow.Of(
                "Held",
                $"{result.HeldVertexCount.ToString("N0", CultureInfo.InvariantCulture)} vertices kept their height",
                "Vertices inside the region that did not move: on a breakline, wall, graded edge, Protect curve or the border, or on a boundary line."));
        }

        return new ModifierStageReport(rows, warning);
    }

    /// <summary>
    /// The mesh must take at least this many samples across the tile's shorter side to read the image's broad
    /// pattern. 16 shows the shapes of a tile (ridges, ramps, blobs) and keeps an ordinary 2.5 m terrain mesh on a
    /// 40 m or larger tile quiet; fewer samples lose the shapes themselves.
    /// </summary>
    internal const double ImageMinSamplesAcrossTile = 16.0;

    /// <summary>The Remesh suggested for an image: this many edges across the tile's shorter side.</summary>
    internal const double ImageSuggestedSamplesAcrossTile = 64.0;

    /// <summary>
    /// The most faces worth asking a Remesh for over the displaced region; beyond it the build stops being
    /// interactive, so the advice is to change the tile or the image rather than the mesh.
    /// </summary>
    internal const double ImageFaceBudget = 2_000_000.0;

    /// <summary>
    /// The warning for an image the mesh samples too coarsely, or null. Judged against the tile, not the pixel:
    /// a pixel is usually far finer than any terrain mesh, so comparing against it would warn on nearly every
    /// image. Warns when the median edge exceeds a 16th of the tile's shorter side (or one pixel, for an image
    /// too small to have 16 pixels across).
    /// </summary>
    internal static string? DescribeImageUndersampling(
        TerrainDisplacer.Result result,
        ImageDisplacementSource image,
        Func<double, string> length)
    {
        double median = result.MedianEdgeLength;
        double pixel = image.PixelSize;
        double shorter = Math.Min(image.TileSizeX, image.TileSizeY);
        double threshold = Math.Max(shorter / ImageMinSamplesAcrossTile, pixel);
        if (!(median > threshold))
            return null;

        // Never finer than a pixel (that adds no image detail), never coarser than the warning threshold.
        double edge = Math.Min(Math.Max(RoundDownReadable(shorter / ImageSuggestedSamplesAcrossTile), pixel), threshold);
        double area = Math.Max(0.0, result.MaxX - result.MinX) * Math.Max(0.0, result.MaxY - result.MinY);
        double faces = 2.0 * area / (edge * edge);

        string prefix =
            $"The terrain here is sampled every {length(median)}, too coarse to read the image: it needs at least " +
            $"{ImageMinSamplesAcrossTile.ToString("0", CultureInfo.InvariantCulture)} samples across the Tile Size's shorter side ({length(shorter)}), so its pattern is lost. ";
        if (faces > ImageFaceBudget)
        {
            return prefix +
                $"An Edge Length of {length(edge)} would make about {(faces / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)} million faces, " +
                "too many to build, so fine detail cannot be resolved at this size. Use a larger Tile Size or a smaller image.";
        }

        return prefix + $"Add a Remesh below this card with an Edge Length of {length(edge)} or less.";
    }

    /// <summary>The largest 1, 2 or 5 times a power of ten at or below the value, so a suggested length reads cleanly.</summary>
    internal static double RoundDownReadable(double value)
    {
        if (!(value > 0.0) || !double.IsFinite(value))
            return value;

        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(value)));
        double lead = value / magnitude;
        double step = lead >= 5.0 ? 5.0 : lead >= 2.0 ? 2.0 : 1.0;
        return step * magnitude;
    }

    private static string DescribeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "(none)";
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static readonly IReadOnlyList<(string Key, string Label)> SourceOptions = new[]
    {
        (DisplaceSources.Noise, "Noise"),
        (DisplaceSources.Image, "Image"),
    };

    private static readonly IReadOnlyList<(string Key, string Label)> WrapOptions = new[]
    {
        (DisplaceSources.Repeat, "Repeat"),
        (DisplaceSources.Clamp, "Clamp"),
        (DisplaceSources.Once, "Once"),
    };

    private static bool IsNoise(ModifierDefinition m) => !DisplaceSources.IsImage((DisplaceModifierDefinition)m);

    private static bool IsImage(ModifierDefinition m) => DisplaceSources.IsImage((DisplaceModifierDefinition)m);

    /// <summary>An image placed by a rectangle: the typed placement rows give way to the curve.</summary>
    private static bool IsMapped(ModifierDefinition m) => IsImage(m) && ((DisplaceModifierDefinition)m).Mapping.HasReferences;

    private static bool IsTypedImage(ModifierDefinition m) => IsImage(m) && !IsMapped(m);

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Choice(
            "Source", "Source", SourceOptions,
            m => ((DisplaceModifierDefinition)m).Source,
            (m, v) => ((DisplaceModifierDefinition)m).Source = v ?? DisplaceSources.Noise,
            "Noise raises and lowers the ground in random bumps from a seed, the same on every machine. Image raises and lowers it by a greyscale image laid on the ground at a real-world size: white up, black down."),
        ModifierParam.Sources(
            "Boundaries", "Boundaries",
            m => ((DisplaceModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve,
            "Closed curves that limit the edit to the terrain inside them. Nothing outside moves; inside, the offset rises from zero at the drawn line to full strength at the Feather distance. Leave empty to displace the whole terrain."),
        ModifierParam.Sources(
            "Protect", "Protect",
            m => ((DisplaceModifierDefinition)m).Protect,
            RhinoObjectType.Curve,
            "Curves that keep their height while the ground around them moves. Breaklines, wall rails, graded edges and walls from the cards below are always kept; add any other line that must stay put."),
        ModifierParam.Number(
            "Feather", "Feather",
            m => ((DisplaceModifierDefinition)m).Feather,
            (m, v) => ((DisplaceModifierDefinition)m).Feather = v,
            "How far the offset takes to fade in, from a boundary inward and from every held line (breaklines, walls, Protect curves, the border) outward. 0 starts at full strength right beside them.",
            min: 0.0,
            unit: ParameterUnit.ModelLength),
        ModifierParam.Bool(
            "HoldBorder", "Hold Border",
            m => ((DisplaceModifierDefinition)m).HoldBorder,
            (m, v) => ((DisplaceModifierDefinition)m).HoldBorder = v,
            "Keep the terrain's outer edge at its height, so it still meets whatever lies beyond it. Untick to displace right out to the edge."),
        ModifierParam.Number(
            "Height", "Height",
            m => ((DisplaceModifierDefinition)m).Height,
            (m, v) => ((DisplaceModifierDefinition)m).Height = v,
            "The full range of the offset. Noise: from the lowest dip to the highest bump, so no point moves more than half of it; most bumps are smaller. Image: the difference between black and white. A negative Height turns the pattern upside down.",
            min: null,
            unit: ParameterUnit.ModelLength,
            liveScrub: true),
        ModifierParam.Number(
            "NoiseSize", "Noise Size",
            m => ((DisplaceModifierDefinition)m).NoiseSize,
            (m, v) => ((DisplaceModifierDefinition)m).NoiseSize = v,
            "The plan width of one bump of the coarsest layer of noise. Each further octave adds bumps half the size.",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            visibleWhen: IsNoise),
        ModifierParam.Slider(
            "Octaves", "Octaves",
            m => ((DisplaceModifierDefinition)m).Octaves,
            (m, v) => ((DisplaceModifierDefinition)m).Octaves = (int)Math.Round(v),
            softMin: 1.0, softMax: GradientNoise.MaxOctaves, decimalPlaces: 0, hardMin: 1.0, hardMax: GradientNoise.MaxOctaves, liveScrub: true,
            help: "How many layers of ever finer bumps to add. 1 is smooth rolling ground; more adds detail, which the terrain can only show where its edges are short enough.",
            visibleWhen: IsNoise),
        ModifierParam.Slider(
            "Roughness", "Roughness",
            m => ((DisplaceModifierDefinition)m).Roughness,
            (m, v) => ((DisplaceModifierDefinition)m).Roughness = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 2, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "How strong each finer octave is next to the one before: low keeps the ground smooth, 1 makes the fine bumps as tall as the coarse ones.",
            visibleWhen: IsNoise),
        ModifierParam.Number(
            "Seed", "Seed",
            m => ((DisplaceModifierDefinition)m).Seed,
            (m, v) => ((DisplaceModifierDefinition)m).Seed = (int)Math.Round(Math.Clamp(v, int.MinValue, int.MaxValue)),
            "Picks a different random pattern. The same seed always gives the same ground.",
            min: null,
            decimalPlaces: 0,
            step: 1.0,
            visibleWhen: IsNoise),
        ModifierParam.FilePath(
            "ImagePath", "Image",
            m => ((DisplaceModifierDefinition)m).ImagePath,
            (m, v) => ((DisplaceModifierDefinition)m).ImagePath = v ?? string.Empty,
            "Images",
            HeightmapImageLoader.Extensions,
            "A greyscale image file (PNG, JPEG, BMP, GIF or TIFF), read as brightness. The document keeps only its path: save over the image and the terrain rebuilds; move or delete it and the card stops until you point it at the file again.",
            visibleWhen: IsImage),
        ModifierParam.Sources(
            "Mapping", "Mapping",
            m => ((DisplaceModifierDefinition)m).Mapping,
            RhinoObjectType.Curve,
            "Optional: one rectangle drawn in plan that places the image. Its start point is the image's bottom-left corner, and the image's bottom edge runs from there counter-clockwise around the rectangle, so the drawing direction never flips the image; start at another corner to turn it. Its sides are the tile's width and height. While set it replaces Origin, Tile Width, Tile Height and Rotation, and moving, turning or scaling it in Rhino moves the image.",
            visibleWhen: IsImage),
        ModifierParam.Number(
            "TileSizeX", "Tile Width",
            m => ((DisplaceModifierDefinition)m).TileSizeX,
            (m, v) => ((DisplaceModifierDefinition)m).TileSizeX = v,
            "How wide the whole image is on the ground, along its own X axis.",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            visibleWhen: IsTypedImage),
        ModifierParam.Number(
            "TileSizeY", "Tile Height",
            m => ((DisplaceModifierDefinition)m).TileSizeY,
            (m, v) => ((DisplaceModifierDefinition)m).TileSizeY = v,
            "How tall the whole image is on the ground, along its own Y axis (towards the image's top).",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            visibleWhen: IsTypedImage),
        ModifierParam.Slider(
            "Neutral", "Neutral",
            m => ((DisplaceModifierDefinition)m).Neutral,
            (m, v) => ((DisplaceModifierDefinition)m).Neutral = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 2, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "The brightness that leaves the ground where it is: 0.5 is mid grey (white raises, black lowers), 0 makes black neutral so the image only raises.",
            visibleWhen: IsImage),
        ModifierParam.Choice(
            "Wrap", "Wrap", WrapOptions,
            m => ((DisplaceModifierDefinition)m).Wrap,
            (m, v) => ((DisplaceModifierDefinition)m).Wrap = v ?? DisplaceSources.Repeat,
            "Repeat lays the image edge to edge in every direction. Clamp lays it once and carries its edge pixels on outward. Once lays it once and leaves the ground alone beyond it; draw a boundary with a Feather to soften its edge.",
            visibleWhen: IsImage),
        ModifierParam.Number(
            "Rotation", "Rotation",
            m => ((DisplaceModifierDefinition)m).Rotation,
            (m, v) => ((DisplaceModifierDefinition)m).Rotation = v,
            "Turns the pattern counter-clockwise about its origin.",
            min: -360.0,
            max: 360.0,
            decimalPlaces: 2,
            unit: ParameterUnit.Degrees,
            visibleWhen: m => !IsMapped(m)),
        ModifierParam.Number(
            "OriginX", "Origin X",
            m => ((DisplaceModifierDefinition)m).OriginX,
            (m, v) => ((DisplaceModifierDefinition)m).OriginX = v,
            "World X of the pattern's origin: the image's lower-left corner, and the point the pattern turns about.",
            min: null,
            unit: ParameterUnit.ModelLength,
            visibleWhen: m => !IsMapped(m)),
        ModifierParam.Number(
            "OriginY", "Origin Y",
            m => ((DisplaceModifierDefinition)m).OriginY,
            (m, v) => ((DisplaceModifierDefinition)m).OriginY = v,
            "World Y of the pattern's origin: the image's lower-left corner, and the point the pattern turns about.",
            min: null,
            unit: ParameterUnit.ModelLength,
            visibleWhen: m => !IsMapped(m)),
        ModifierParam.Bool(
            "ShowMapping", "Show Mapping",
            m => ((DisplaceModifierDefinition)m).ShowMapping,
            (m, v) => ((DisplaceModifierDefinition)m).ShowMapping = v,
            "Draw the image tiles over the terrain, with the origin and the image's top edge, while this card is on. Drawn only, never baked.",
            visibleWhen: IsImage),
    };
}
