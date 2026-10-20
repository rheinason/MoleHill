namespace MoleHill.Core.Scan;

/// <summary>Plan stations of a supported continuation across missing observations.</summary>
public readonly record struct ScanInferredInterval(double Start, double End);
