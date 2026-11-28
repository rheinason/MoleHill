// The link a profile-mode section curve carries to its plan curve, stored on the curve's own attributes.
using System.Globalization;
using MoleHill.Core.Analysis;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// What a section curve drawn by profile mode knows: which plan curve it is the long section of, where its
/// frame is (the fixed grid the overlay draws, not the curve's position), and a fingerprint of the plan curve
/// it was drawn from.
///
/// <para>It lives on the curve's attributes because Rhino copies attributes to whatever a Trim, Split or Join
/// leaves, and an Undo of Accept brings the curve back with them: the link travels with the geometry the user
/// is editing, with no separate record to fall out of step. The frame is data, not position, so moving the
/// curve against the grid means what it shows.</para>
/// </summary>
internal sealed record ProfileSection(Guid PlanId, LongSectionFrame Frame, double PlaneZ, string PlanHash)
{
    private const string Prefix = "MoleHill.ProfileMode.";
    private const string PlanKey = Prefix + "Plan";
    private const string OriginXKey = Prefix + "OriginX";
    private const string OriginYKey = Prefix + "OriginY";
    private const string DatumKey = Prefix + "Datum";
    private const string ExaggerationKey = Prefix + "Exaggeration";
    private const string PlaneZKey = Prefix + "PlaneZ";
    private const string HashKey = Prefix + "PlanHash";

    /// <summary>True when the attributes carry a section link (cheap: one user-string lookup).</summary>
    public static bool IsSection(ObjectAttributes attributes) => !string.IsNullOrEmpty(attributes.GetUserString(PlanKey));

    public static ProfileSection? Read(ObjectAttributes attributes)
    {
        if (!Guid.TryParse(attributes.GetUserString(PlanKey), out Guid plan) ||
            !TryRead(attributes, OriginXKey, out double ox) ||
            !TryRead(attributes, OriginYKey, out double oy) ||
            !TryRead(attributes, DatumKey, out double datum) ||
            !TryRead(attributes, ExaggerationKey, out double exaggeration) || !(exaggeration > 0.0) ||
            !TryRead(attributes, PlaneZKey, out double planeZ))
            return null;
        return new ProfileSection(plan, new LongSectionFrame(ox, oy, datum, exaggeration), planeZ, attributes.GetUserString(HashKey) ?? string.Empty);
    }

    public void Write(ObjectAttributes attributes)
    {
        attributes.SetUserString(PlanKey, PlanId.ToString());
        attributes.SetUserString(OriginXKey, Format(Frame.OriginX));
        attributes.SetUserString(OriginYKey, Format(Frame.OriginY));
        attributes.SetUserString(DatumKey, Format(Frame.Datum));
        attributes.SetUserString(ExaggerationKey, Format(Frame.Exaggeration));
        attributes.SetUserString(PlaneZKey, Format(PlaneZ));
        attributes.SetUserString(HashKey, PlanHash);
    }

    public Point3d ToWorld(double station, double elevation)
    {
        (double x, double y) = Frame.ToDrawing(station, elevation);
        return new Point3d(x, y, PlaneZ);
    }

    /// <summary>A curve in the section as a flat [station, elevation, ...] polyline, in its own direction.</summary>
    public double[]? ToStations(Curve curve, double tolerance)
    {
        Polyline? polyline = null;
        if (curve.TryGetPolyline(out Polyline exact))
            polyline = exact;
        else
        {
            // Measured in the drawing, where heights are exaggerated: sample finely enough for true heights.
            double fine = tolerance / Math.Max(1.0, Frame.Exaggeration);
            using PolylineCurve? sampled = curve.ToPolyline(0, 0, global::Rhino.RhinoMath.ToRadians(1.0), 0.0, 0.0, fine, 0.0, 0.0, true);
            if (sampled != null && sampled.TryGetPolyline(out Polyline fit))
                polyline = fit;
        }

        if (polyline == null || polyline.Count < 2)
            return null;
        var flat = new double[polyline.Count * 2];
        for (int i = 0; i < polyline.Count; i++)
        {
            (double station, double elevation) = Frame.FromDrawing(polyline[i].X, polyline[i].Y);
            flat[i * 2] = station;
            flat[i * 2 + 1] = elevation;
        }

        return flat;
    }

    /// <summary>A fingerprint of a plan path: the section reads stations off it, so it must not have changed.</summary>
    public static string Hash(double[] path)
    {
        // FNV-1a over the coordinates' bits: a change detector, not a security hash.
        ulong hash = 14695981039346656037UL;
        foreach (byte b in System.Runtime.InteropServices.MemoryMarshal.AsBytes(path.AsSpan()))
            hash = (hash ^ b) * 1099511628211UL;
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static bool TryRead(ObjectAttributes attributes, string key, out double value) =>
        double.TryParse(attributes.GetUserString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
