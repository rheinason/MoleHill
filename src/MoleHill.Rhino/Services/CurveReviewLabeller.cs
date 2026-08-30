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
    /// <summary>Sublayer that placed labels are written to, under the terrain's annotation layer.</summary>
    private const string LabelLayerSuffix = "::Labels";

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
                    string preview = BuildLabel(analysis, e.CurrentPoint, content);
                    if (preview.Length > 0)
                        e.Display.DrawDot(e.CurrentPoint, preview, System.Drawing.Color.FromArgb(58, 58, 62), System.Drawing.Color.White);
                };

                GetResult result = getPoint.Get();
                if (result != GetResult.Point)
                    break;

                Point3d point = getPoint.Point();
                string text = BuildLabel(analysis, point, content);
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
    public static string BuildLabel(CurveReviewAnalysis analysis, Point3d point, CurveLabelContent content)
    {
        if (!point.IsValid)
            return string.Empty;

        var parts = new List<string>(4);
        CurveReviewSample? sample = FindNearestSample(analysis, point);

        if (content.HasFlag(CurveLabelContent.Station) && sample.HasValue)
            parts.Add($"STA {sample.Value.Station.ToString("F1", CultureInfo.CurrentCulture)}");

        if (content.HasFlag(CurveLabelContent.Elevation))
            parts.Add(point.Z.ToString("F2", CultureInfo.CurrentCulture));

        if (content.HasFlag(CurveLabelContent.Grade) && TryFindSpanGrade(analysis, point, out double grade))
            parts.Add(grade.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + "%");

        if (content.HasFlag(CurveLabelContent.CutFill) && sample is { HasTerrain: true } terrainSample)
        {
            double delta = point.Z - terrainSample.TerrainZ;
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
    /// The annotation label sublayer of the selected terrain, created if needed. Falls back to MoleHill's
    /// default annotation layer when no terrain is selected, so labelling works on a bare curve too.
    /// </summary>
    private static int EnsureLabelLayer(RhinoDoc doc)
    {
        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        string root = TerrainDefinition.ResolveAnnotationLayerPath(terrain?.AnnotationLayerPath);
        return TerrainController.EnsureLayerPath(doc, root + LabelLayerSuffix);
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

    /// <summary>Grade of the stretch the point falls in, matched by station rather than by proximity so a
    /// pick near a kink reports the stretch it is actually on.</summary>
    private static bool TryFindSpanGrade(CurveReviewAnalysis analysis, Point3d point, out double grade)
    {
        grade = 0.0;
        if (analysis.Spans.Count == 0)
            return false;

        CurveReviewSample? sample = FindNearestSample(analysis, point);
        if (sample is not { } station)
            return false;

        foreach (CurveReviewSpan span in analysis.Spans)
        {
            if (station.Station >= span.StartStation && station.Station <= span.EndStation)
            {
                grade = span.Grade;
                return true;
            }
        }

        grade = station.Station <= analysis.Spans[0].StartStation
            ? analysis.Spans[0].Grade
            : analysis.Spans[^1].Grade;
        return true;
    }
}
