using System.Globalization;
using MoleHill.Core.Processing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>One layer/group/Undo writer for every curve extraction source, including optional scan extras.</summary>
internal static class TerrainCurveExtractionOutput
{
    internal static readonly (string Name, System.Drawing.Color Color)[] OutputLayers = new[]
    {
        TerrainCurveExtractionStyle.Border, TerrainCurveExtractionStyle.Creases,
        TerrainCurveExtractionStyle.Contours, TerrainCurveExtractionStyle.Spots,
    }.Select(style => (style.Layer, style.Colour)).ToArray();

    /// <summary>
    /// Adds the curves and points on their layers under a fresh root named <paramref name="rootName"/> (" 2",
    /// " 3" when taken), named by role, grouped, in one undo step. <paramref name="addExtra"/> adds more
    /// objects in the same step: it gets a function from a layer (name and colour) to the attributes for it.
    /// </summary>
    internal static int Add(
        RhinoDoc doc,
        TerrainCurveRecovery.Result recovery,
        string rootName,
        string undoName,
        Func<Func<string, System.Drawing.Color, string, ObjectAttributes>, IEnumerable<Guid>>? addExtra,
        out string root)
    {
        root = SurveyLayerNaming.NextFreeRootName(
            rootName.Trim(),
            candidate => doc.Layers.FindByFullPath(candidate, -1) >= 0);
        var ids = new List<Guid>();
        uint undoRecord = doc.BeginUndoRecord(undoName);
        try
        {
            var layers = new Dictionary<string, int>(StringComparer.Ordinal);
            string layerRoot = root;
            ObjectAttributes OnLayer(string kind, System.Drawing.Color color, string name)
            {
                if (!layers.TryGetValue(kind, out int index))
                {
                    index = EnsureLayer(doc, layerRoot, kind, color);
                    layers[kind] = index;
                }

                var attributes = new ObjectAttributes { Name = name, LayerIndex = index };
                attributes.SetUserString("MoleHill.Recovery.MaximumDeviation", recovery.MaximumDeviation.ToString("G17", CultureInfo.InvariantCulture));
                attributes.SetUserString("MoleHill.Recovery.Termination", recovery.Termination.ToString());
                attributes.SetUserString("MoleHill.Recovery.PrunedInputs", recovery.PrunedInputCount.ToString(CultureInfo.InvariantCulture));
                if (recovery.OriginalMaximumDeviation is double original)
                    attributes.SetUserString("MoleHill.Recovery.OriginalMaximumDeviation", original.ToString("G17", CultureInfo.InvariantCulture));
                return attributes;
            }

            ObjectAttributes On(string kind, string name) =>
                OnLayer(kind, OutputLayers.First(layer => layer.Name == kind).Color, name);

            foreach (double[] border in recovery.Borders)
                ids.Add(doc.Objects.AddPolyline(ToPoints(border, close: true), On("Border", "Border")));

            foreach (double[] line in recovery.CreaseLines)
                ids.Add(doc.Objects.AddPolyline(ToPoints(line, close: false), On("Creases", "Crease")));

            foreach (TerrainCurveRecovery.RecoveredContour contour in recovery.Contours)
            {
                string name = $"Contour {contour.Level.ToString("F3", CultureInfo.InvariantCulture)}";
                using Curve curve = TerrainCurveExtractionGeometry.Contour(contour);
                ids.Add(doc.Objects.AddCurve(curve, On("Contours", name)));
            }

            for (int i = 0; i < recovery.SpotPoints.Length; i += 3)
            {
                var point = new Point3d(recovery.SpotPoints[i], recovery.SpotPoints[i + 1], recovery.SpotPoints[i + 2]);
                ids.Add(doc.Objects.AddPoint(point, On("Spot heights", "Spot")));
            }

            if (addExtra != null)
                ids.AddRange(addExtra(OnLayer));

            ids.RemoveAll(id => id == Guid.Empty);
            if (ids.Count > 0)
                doc.Groups.Add(ids);
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }

        return ids.Count;
    }

    /// <summary>The root layer and the kind's layer under it, created with the kind's colour if missing.</summary>
    private static int EnsureLayer(RhinoDoc doc, string root, string kind, System.Drawing.Color color)
    {
        int parent = doc.Layers.FindByFullPath(root, -1);
        if (parent < 0)
            parent = doc.Layers.Add(new Layer { Name = root });

        string path = $"{root}::{kind}";
        int index = doc.Layers.FindByFullPath(path, -1);
        if (index >= 0)
            return index;

        return doc.Layers.Add(new Layer { Name = kind, ParentLayerId = doc.Layers[parent].Id, Color = color });
    }

    private static List<Point3d> ToPoints(double[] xyz, bool close)
    {
        var points = new List<Point3d>(xyz.Length / 3 + 1);
        for (int i = 0; i < xyz.Length; i += 3)
            points.Add(new Point3d(xyz[i], xyz[i + 1], xyz[i + 2]));
        if (close && points.Count > 2)
            points.Add(points[0]);
        return points;
    }
}
