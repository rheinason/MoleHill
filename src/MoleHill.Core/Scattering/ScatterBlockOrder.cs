namespace MoleHill.Core.Scattering;

/// <summary>
/// Curve scatter only: how blocks from a multi-block mix are assigned to successive along-curve slots.
/// </summary>
public enum ScatterBlockOrder
{
    /// <summary>Each slot picks a block at random, weighted by the block's weight (default).</summary>
    Random = 0,

    /// <summary>Cycle through the block list in order (A→B→C→A…), giving a repeating pattern.</summary>
    Sequence = 1
}
