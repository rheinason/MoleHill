using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal sealed class DirtyRegionPlanner
{
    internal sealed class Plan
    {
        public required Bounds2D DirtyBounds { get; init; }

        public required IReadOnlyList<string> AffectedOwnerKeys { get; init; }
    }

    public static Plan Build(IReadOnlyList<GradingPatch> patches, string changedOwnerKey, double expansion)
    {
        GradingPatch? changed = patches.FirstOrDefault(patch => string.Equals(patch.OwnerKey, changedOwnerKey, StringComparison.Ordinal));
        if (changed == null)
        {
            return new Plan
            {
                DirtyBounds = new Bounds2D(0.0, 0.0, 0.0, 0.0),
                AffectedOwnerKeys = Array.Empty<string>()
            };
        }

        var affected = new List<string>();
        Bounds2D expandedChanged = Expand(changed.DirtyBounds, expansion);
        double minX = expandedChanged.MinX;
        double maxX = expandedChanged.MaxX;
        double minY = expandedChanged.MinY;
        double maxY = expandedChanged.MaxY;

        for (int i = 0; i < patches.Count; i++)
        {
            Bounds2D expandedPatch = Expand(patches[i].DirtyBounds, expansion);
            if (!expandedPatch.Intersects(expandedChanged))
                continue;

            affected.Add(patches[i].OwnerKey);
            if (expandedPatch.MinX < minX) minX = expandedPatch.MinX;
            if (expandedPatch.MaxX > maxX) maxX = expandedPatch.MaxX;
            if (expandedPatch.MinY < minY) minY = expandedPatch.MinY;
            if (expandedPatch.MaxY > maxY) maxY = expandedPatch.MaxY;
        }

        return new Plan
        {
            DirtyBounds = new Bounds2D(minX, maxX, minY, maxY),
            AffectedOwnerKeys = affected
        };
    }

    private static Bounds2D Expand(Bounds2D bounds, double expansion)
    {
        return new Bounds2D(
            bounds.MinX - expansion,
            bounds.MaxX + expansion,
            bounds.MinY - expansion,
            bounds.MaxY + expansion);
    }
}
