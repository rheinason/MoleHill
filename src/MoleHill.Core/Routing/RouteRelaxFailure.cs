namespace MoleHill.Core.Routing;

/// <summary>Why a sketch could not be relaxed. A host turns these into its own wording.</summary>
public enum RouteRelaxFailure
{
    None,

    /// <summary>A grade limit, weight or width that cannot be used.</summary>
    InvalidSettings,

    /// <summary>The sketch has fewer than two distinct points.</summary>
    NoLength,

    /// <summary>The sketch leaves the terrain.</summary>
    OffTerrain,
}
