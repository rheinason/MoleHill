namespace MoleHill.Core.Scattering;

/// <summary>One curve-scatter sample: the (jittered) XY position plus the curve tangent direction at the
/// underlying on-curve point, in radians (atan2(dy, dx)). The tangent lets callers orient instances along
/// the curve (align-to-tangent); it reflects the curve direction before XY jitter is applied.</summary>
public readonly record struct ScatterCurvePoint(double X, double Y, double TangentRadians);
