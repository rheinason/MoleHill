using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Converts between the persisted tile list on <see cref="SculptModifierDefinition"/> (base64-wrapped,
/// deflate-compressed float32 — FieldVersion 1) and the runtime <see cref="SculptDisplacementField"/>.
/// </summary>
internal static class SculptFieldCodec
{
    public static SculptDisplacementField Decode(SculptModifierDefinition sculpt)
    {
        var field = new SculptDisplacementField(sculpt.EffectiveCellSize);
        foreach (var tile in sculpt.Tiles)
        {
            if (string.IsNullOrEmpty(tile.D))
                continue;

            try
            {
                field.ReplaceTile(tile.I, tile.J, SculptDisplacementField.DecodeTile(Convert.FromBase64String(tile.D)));
            }
            catch (FormatException)
            {
                // Corrupt tile payload: skip rather than fail the whole terrain build.
            }
            catch (InvalidDataException)
            {
            }
        }

        return field;
    }

    public static List<SculptTile> Encode(SculptDisplacementField field)
    {
        var tiles = new List<SculptTile>(field.Tiles.Count);
        foreach (var ((i, j), samples) in field.Tiles)
        {
            tiles.Add(new SculptTile
            {
                I = i,
                J = j,
                D = Convert.ToBase64String(SculptDisplacementField.EncodeTile(samples)),
            });
        }

        tiles.Sort((a, b) => a.I != b.I ? a.I.CompareTo(b.I) : a.J.CompareTo(b.J));
        return tiles;
    }

    /// <summary>Approximate persisted size of the field payloads in kilobytes (for the card readout).</summary>
    public static double EstimateKilobytes(SculptModifierDefinition sculpt)
    {
        long chars = 0;
        foreach (var tile in sculpt.Tiles)
            chars += tile.D.Length;
        return chars * 3.0 / 4.0 / 1024.0;
    }
}
