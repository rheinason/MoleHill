namespace MoleHill.Core.Scan;

public enum ScanRoofKind
{
    Flat,
    Shed,
    Gable,
    Hip,
    HalfHip,
    CrossGable,
    PlaneArrangement,
    /// <summary>Parts split along the facades, each under its own simple roof.</summary>
    Composite,
    ExtrusionFallback,
}
