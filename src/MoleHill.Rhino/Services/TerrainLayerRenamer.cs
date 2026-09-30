using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

/// <summary>
/// One layer to rename when a terrain is renamed: the layer at <see cref="OldPath"/> takes the name
/// <see cref="NewName"/>, which puts it, and everything beneath it, at <see cref="NewPath"/>.
/// </summary>
internal readonly record struct TerrainLayerMove(string OldPath, string NewName, string NewPath)
{
    public int Depth => OldPath.Split(new[] { "::" }, StringSplitOptions.None).Length;
}

/// <summary>
/// Moves a terrain's layer tree when the terrain is renamed, so its output, baked geometry and owned
/// inputs stay together under the new name instead of being stranded under the old one while a fresh
/// tree grows beside it.
///
/// <see cref="Plan"/> is pure: it compares the role table a terrain had under its old name with the
/// one it has under its new name. <see cref="Apply"/> is the only part that touches the document.
/// </summary>
internal static class TerrainLayerRenamer
{
    private static readonly string[] Separator = { "::" };

    /// <summary>
    /// The layers that have to be renamed to turn <paramref name="oldTable"/>'s layout into
    /// <paramref name="newTable"/>'s: for every role, the first segment where the two paths differ
    /// names a layer that is the same layer under a new name. Deepest first, so each old path is still
    /// valid when its turn comes.
    /// </summary>
    public static IReadOnlyList<TerrainLayerMove> Plan(LayerRoleTable oldTable, LayerRoleTable newTable)
    {
        var moves = new Dictionary<string, TerrainLayerMove>(StringComparer.OrdinalIgnoreCase);

        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            AddMove(moves, oldTable.Path(role), newTable.Path(role));

        return moves.Values
            .OrderByDescending(move => move.Depth)
            .ThenBy(move => move.OldPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The path after a rename, or null when it is not under any moved layer. The deepest moved layer
    /// that contains it decides, because that is the one whose new name it inherits.
    /// </summary>
    public static string? Rewrite(string layerPath, IReadOnlyList<TerrainLayerMove> moves)
    {
        TerrainLayerMove? best = null;
        foreach (var move in moves)
        {
            bool contains = layerPath.Equals(move.OldPath, StringComparison.OrdinalIgnoreCase)
                || layerPath.StartsWith(move.OldPath + "::", StringComparison.OrdinalIgnoreCase);
            if (contains && (best == null || move.OldPath.Length > best.Value.OldPath.Length))
                best = move;
        }

        if (best == null)
            return null;

        return best.Value.NewPath + layerPath[best.Value.OldPath.Length..];
    }

    /// <summary>
    /// Rewrites every source layer the terrain reads that sits under a moved layer, so a renamed
    /// terrain keeps reading the inputs that moved with it.
    /// </summary>
    public static int RewriteSources(TerrainDefinition terrain, IReadOnlyList<TerrainLayerMove> moves)
    {
        int rewritten = 0;
        foreach (SourceReferenceSet set in terrain.EnumerateSourceSets())
        {
            if (set.LayerPaths.Count == 0)
                continue;

            var updated = new List<string>(set.LayerPaths.Count);
            bool changed = false;
            foreach (string path in set.LayerPaths)
            {
                string? moved = Rewrite(path, moves);
                if (moved != null)
                {
                    changed = true;
                    rewritten++;
                }

                updated.Add(moved ?? path);
            }

            if (changed)
                set.ReplaceLayers(updated);
        }

        return rewritten;
    }

    /// <summary>
    /// The first destination that is already taken by a layer the terrain does not own, or null when
    /// the whole move is clear. A move whose old layer is absent has nothing to rename and cannot collide.
    /// </summary>
    public static string? FindCollision(RhinoDoc doc, IReadOnlyList<TerrainLayerMove> moves)
    {
        foreach (var move in moves)
        {
            if (doc.Layers.FindByFullPath(move.OldPath, -1) < 0)
                continue;

            if (doc.Layers.FindByFullPath(move.NewPath, -1) >= 0)
                return move.NewPath;
        }

        return null;
    }

    /// <summary>Renames each layer that exists. Returns how many were renamed.</summary>
    public static int Apply(RhinoDoc doc, IReadOnlyList<TerrainLayerMove> moves)
    {
        int renamed = 0;
        foreach (var move in moves)
        {
            int index = doc.Layers.FindByFullPath(move.OldPath, -1);
            if (index < 0)
                continue;

            Layer layer = doc.Layers[index];
            layer.Name = move.NewName;
            if (doc.Layers.Modify(layer, index, quiet: true))
                renamed++;
        }

        return renamed;
    }

    private static void AddMove(Dictionary<string, TerrainLayerMove> moves, string oldPath, string newPath)
    {
        string[] oldSegments = oldPath.Split(Separator, StringSplitOptions.None);
        string[] newSegments = newPath.Split(Separator, StringSplitOptions.None);

        int limit = Math.Min(oldSegments.Length, newSegments.Length);
        for (int i = 0; i < limit; i++)
        {
            if (string.Equals(oldSegments[i], newSegments[i], StringComparison.OrdinalIgnoreCase))
                continue;

            string oldPrefix = string.Join("::", oldSegments.Take(i + 1));
            string newPrefix = string.Join("::", newSegments.Take(i + 1));
            moves[oldPrefix] = new TerrainLayerMove(oldPrefix, newSegments[i], newPrefix);
            return;
        }
    }
}
