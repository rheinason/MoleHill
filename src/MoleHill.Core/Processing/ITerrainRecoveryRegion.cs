namespace MoleHill.Core.Processing;

/// <summary>The portion of a reference terrain to measure and describe with recovered contours and spots.</summary>
public interface ITerrainRecoveryRegion
{
    bool Contains(double x, double y);
    IReadOnlyList<RegionInputClipper.InputPolyline> Clip(double[] points, bool closed);
}
