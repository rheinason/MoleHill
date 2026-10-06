// Places text-dot labels along the inspected curve from the live curve inspector.
using System.Globalization;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>What a placed label says. Combinable — a label may carry several readings on one line.</summary>
[Flags]
internal enum CurveLabelContent
{
    None = 0,
    Elevation = 1,
    Grade = 2,
    Station = 4,
    CutFill = 8
}

/// <summary>
/// The inspector's labelling pass: pick points along the inspected curve, drop a text dot at each.
///
/// This replaces the old <c>mhSlopeCheckAndMark</c> command. Two things were wrong with that as a separate
/// command: it recomputed slope from scratch when the inspector already holds a full station profile, and
/// it dropped bare text dots on whatever layer happened to be current. Here the readings come from the
/// analysis already on screen, and the dots land on a MoleHill annotation sublayer whose print width and
/// colour the layer table owns.
/// </summary>
internal static class CurveReviewLabeller
{
    /// <summary>
    /// Runs the pick loop until the user presses Enter or Escape. Returns how many labels were placed.
    /// The whole session is one undo record, so a run of labels is undone as a unit rather than one dot
    /// at a time.
    /// </summary>
    public static int Run(RhinoDoc doc, Guid objectId, CurveReviewAnalysis analysis, CurveLabelContent content)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(analysis);

        if (doc.Objects.FindId(objectId)?.Geometry is not Curve curve)
        {
            RhinoApp.WriteLine("Curve is no longer available to label.");
            return 0;
        }

        if (content == CurveLabelContent.None)
            content = CurveLabelContent.Elevation;

        // Readings are taken at the picked point itself, not at the nearest analysis sample: samples sit
        // up to half a spacing apart, and a baked dot that is off by that much is simply wrong. One plan
        // projection serves every pick in the session.
        using Curve? planCurve = GeometryCommandAlgorithms.CreatePlanCurve(curve);
        var reader = new PointReader(curve, planCurve, CurveReviewService.PeekTerrainMesh(doc), doc.ModelAbsoluteTolerance);

        int layerIndex = EnsureLabelLayer(doc);
        uint undoRecord = doc.BeginUndoRecord("Label curve");
        int placed = 0;
        try
        {
            while (true)
            {
                var getPoint = new GetPoint();
                getPoint.SetCommandPrompt($"Pick a point on the curve to label ({Describe(content)}), or press Enter to finish");
                getPoint.AcceptNothing(true);
                getPoint.Constrain(curve, false);

                // The inspector overlay stays live behind the get, so the picked station can be read
                // against the ribbon it is being placed on.
                getPoint.DynamicDraw += (_, e) =>
                {
                    string preview = BuildLabel(analysis, reader, e.CurrentPoint, content);
                    if (preview.Length > 0)
                        e.Display.DrawDot(e.CurrentPoint, preview, System.Drawing.Color.FromArgb(58, 58, 62), System.Drawing.Color.White);
                };

                GetResult result = getPoint.Get();
                if (result != GetResult.Point)
                    break;

                Point3d point = getPoint.Point();
                string text = BuildLabel(analysis, reader, point, content);
                if (text.Length == 0)
                    continue;

                var attributes = doc.CreateDefaultAttributes();
                attributes.LayerIndex = layerIndex;
                if (doc.Objects.AddTextDot(new TextDot(text, point), attributes) != Guid.Empty)
                    placed++;

                doc.Views.Redraw();
            }
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }

        if (placed > 0)
            RhinoApp.WriteLine($"Placed {placed} label(s) on {doc.Layers[layerIndex].FullPath}.");
        return placed;
    }

    /// <summary>The label text for a point on the curve, assembled from the readings the user asked for.</summary>
    private static string BuildLabel(CurveReviewAnalysis analysis, PointReader reader, Point3d point, CurveLabelContent content)
    {
        if (!point.IsValid)
            return string.Empty;

        var parts = new List<string>(4);
        double station = reader.StationAt(point, analysis);

        if (content.HasFlag(CurveLabelContent.Station) && double.IsFinite(station))
            parts.Add($"STA {station.ToString("F1", CultureInfo.CurrentCulture)}");

        if (content.HasFlag(CurveLabelContent.Elevation))
            parts.Add(point.Z.ToString("F2", CultureInfo.CurrentCulture));

        if (content.HasFlag(CurveLabelContent.Grade) &&
            CurveReviewAnalysis.SpanGradeAtStation(analysis.Spans, station) is double grade)
            parts.Add(grade.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + "%");

        if (content.HasFlag(CurveLabelContent.CutFill) && reader.TryTerrainZAt(point, out double terrainZ))
        {
            double delta = point.Z - terrainZ;
            parts.Add(delta >= 0.0
                ? $"fill {delta.ToString("F2", CultureInfo.CurrentCulture)}"
                : $"cut {(-delta).ToString("F2", CultureInfo.CurrentCulture)}");
        }

        return string.Join("  ", parts);
    }

    /// <summary>Human-readable name for a content combination, for the command prompt.</summary>
    public static string Describe(CurveLabelContent content)
    {
        var names = new List<string>(4);
        if (content.HasFlag(CurveLabelContent.Station))
            names.Add("station");
        if (content.HasFlag(CurveLabelContent.Elevation))
            names.Add("elevation");
        if (content.HasFlag(CurveLabelContent.Grade))
            names.Add("grade");
        if (content.HasFlag(CurveLabelContent.CutFill))
            names.Add("cut/fill");
        return names.Count == 0 ? "elevation" : string.Join(" + ", names);
    }

    /// <summary>
    /// The label layer of the selected terrain, created if needed. With no terrain selected this
    /// resolves through the document's template, so labelling works on a bare curve too.
    /// </summary>
    private static int EnsureLabelLayer(RhinoDoc doc)
    {
        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        return LayerRoleService.EnsureRoleLayer(doc, LayerRole.Labels, terrain: terrain);
    }

    /// <summary>Nearest sampled station to a picked point. Null when the analysis has no samples.</summary>
    private static CurveReviewSample? FindNearestSample(CurveReviewAnalysis analysis, Point3d point)
    {
        CurveReviewSample? best = null;
        double bestDistance = double.MaxValue;
        foreach (CurveReviewSample sample in analysis.Samples)
        {
            double distance = sample.Point.DistanceToSquared(point);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = sample;
        }

        return best;
    }

    /// <summary>Station and terrain readings at an exact picked point on the inspected curve.</summary>
    private sealed class PointReader
    {
        private readonly Curve _curve;
        private readonly Curve? _planCurve;
        private readonly Mesh? _terrainMesh;
        private readonly double _tolerance;

        public PointReader(Curve curve, Curve? planCurve, Mesh? terrainMesh, double tolerance)
        {
            _curve = curve;
            _planCurve = planCurve;
            _terrainMesh = terrainMesh;
            _tolerance = tolerance;
        }

        /// <summary>Plan station of the point — the plan length to its parameter on the curve, the same
        /// measure the analysis stations by. Falls back to the nearest sample only when the curve has no
        /// plan projection.</summary>
        public double StationAt(Point3d point, CurveReviewAnalysis analysis)
        {
            if (_planCurve != null && _curve.ClosestPoint(point, out double parameter))
                return CurveReviewAnalyzer.PlanLengthAt(_planCurve, parameter);

            return FindNearestSample(analysis, point) is { } sample ? sample.Station : double.NaN;
        }

        /// <summary>Terrain elevation straight below or above the point, as the analysis samples it.</summary>
        public bool TryTerrainZAt(Point3d point, out double terrainZ)
        {
            terrainZ = 0.0;
            if (_terrainMesh == null ||
                !TerrainMeshProjection.TryProjectPointAlongWorldZ(_terrainMesh, point, _tolerance, out Point3d projected))
                return false;

            terrainZ = projected.Z;
            return true;
        }
    }
}
