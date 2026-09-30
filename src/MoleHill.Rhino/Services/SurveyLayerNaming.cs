using MoleHill.Core.Interop;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Names the layers a survey import creates. A survey is plain Rhino geometry that belongs to no
/// terrain, so its layers sit in a tree named after the imported file, entirely outside the terrains'
/// own roots. The user decides which terrains read it.
///
/// Pure string logic with no document access, so it is testable without Rhino.
/// </summary>
internal static class SurveyLayerNaming
{
    public const string FallbackRootName = "Survey";

    /// <summary>User-string key stamped on every imported object, so a re-import can find what it made.</summary>
    public const string ImportUserStringKey = "MoleHill.SurveyImport";

    /// <summary>
    /// The root layer name for a survey file: its name without extension, cleaned of anything a Rhino
    /// layer name cannot hold. Never empty.
    /// </summary>
    public static string RootName(string? filePath)
    {
        string name = string.IsNullOrWhiteSpace(filePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(filePath.Trim());

        string cleaned = TerrainLayerNaming.CleanSegment(name);
        return cleaned.Length == 0 ? FallbackRootName : cleaned;
    }

    /// <summary>
    /// The first name not already taken, for "add as a new survey": <c>Name</c>, <c>Name 2</c>,
    /// <c>Name 3</c>. <paramref name="exists"/> answers for a whole root, case-insensitively.
    /// </summary>
    public static string NextFreeRootName(string rootName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(rootName))
            return rootName;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{rootName} {suffix}";
            if (!exists(candidate))
                return candidate;
        }
    }

    /// <summary>The full layer path for a survey-relative path under a root.</summary>
    public static string FullPath(string rootName, string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath) ? rootName : $"{rootName}::{relativePath.Trim()}";

    /// <summary>
    /// Display colour a newly created survey layer starts with, or null to leave Rhino's default. Seeds
    /// the layer once; the user owns it after that. Matches what these inputs were given when they lived
    /// under the terrain root, so a survey stays legible by kind without anyone styling it.
    /// </summary>
    public static int? DefaultColorArgb(FieldCodeRole role) => role switch
    {
        FieldCodeRole.Spot => unchecked((int)0xFF008900),
        FieldCodeRole.Contour => unchecked((int)0xFF8C8C8C),
        FieldCodeRole.Breakline => unchecked((int)0xFFFFC000),
        FieldCodeRole.Boundary => unchecked((int)0xFF1E64FF),
        _ => null
    };
}
