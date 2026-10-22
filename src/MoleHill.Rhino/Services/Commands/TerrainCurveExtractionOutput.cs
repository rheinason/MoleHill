using System.Globalization;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>One layer/group/Undo writer for every curve extraction source, including optional scan extras.</summary>
internal static class TerrainCurveExtractionOutput
{
    /// <summary>
    /// Adds the curves and points to the input layers of a fresh terrain root, <c>MoleHill {name}</c> (" 2", " 3"
    /// when taken), named by role, grouped, in one undo step: a terrain called <c>name</c> reads them from the
    /// layers it already looks at (see <see cref="TerrainCurveExtractionStyle"/>). <paramref name="addExtra"/> adds
    /// more objects in the same step: it gets a function from a style and object name to the attributes for it.
    /// </summary>
    internal static int Add(
        RhinoDoc doc,
        TerrainCurveRecovery.Result recovery,
        string terrainName,
        string undoName,
        Func<Func<TerrainCurveExtractionStyle.Style, string, ObjectAttributes>, IEnumerable<Guid>>? addExtra,
        out string root)
    {
        string name = TerrainLayerNaming.NextFreeName(TerrainLayerNaming.Sanitize(terrainName),
            candidate => doc.Layers.FindByFullPath(TerrainLayerNaming.RootFor(candidate), -1) >= 0);
        root = TerrainLayerNaming.RootFor(name);
        IReadOnlyList<LayerTemplateEntry> template = new LayerTemplateStore().GetDefaultTemplates()[0].Entries;
        var ids = new List<Guid>();
        uint undoRecord = doc.BeginUndoRecord(undoName);
        try
        {
            var layers = new Dictionary<string, int>(StringComparer.Ordinal);
            string layerRoot = root;
            ObjectAttributes OnLayer(TerrainCurveExtractionStyle.Style style, string objectName)
            {
                if (!layers.TryGetValue(style.Layer, out int index))
                {
                    index = EnsureLayer(doc, layerRoot, style.Layer, template);
                    layers[style.Layer] = index;
                }

                // Several kinds share a layer (every breakline is on one), so the kind's colour is the object's.
                var attributes = new ObjectAttributes
                {
                    Name = objectName,
                    LayerIndex = index,
                    ColorSource = ObjectColorSource.ColorFromObject,
                    ObjectColor = style.Colour,
                };
                attributes.SetUserString("MoleHill.Recovery.MaximumDeviation", recovery.MaximumDeviation.ToString("G17", CultureInfo.InvariantCulture));
                attributes.SetUserString("MoleHill.Recovery.Termination", recovery.Termination.ToString());
                attributes.SetUserString("MoleHill.Recovery.PrunedInputs", recovery.PrunedInputCount.ToString(CultureInfo.InvariantCulture));
                if (recovery.OriginalMaximumDeviation is double original)
                    attributes.SetUserString("MoleHill.Recovery.OriginalMaximumDeviation", original.ToString("G17", CultureInfo.InvariantCulture));
                return attributes;
            }

            foreach (double[] border in recovery.Borders)
                ids.Add(doc.Objects.AddPolyline(ToPoints(border, close: true), OnLayer(TerrainCurveExtractionStyle.Border, "Border")));

            foreach (double[] line in recovery.CreaseLines)
                ids.Add(doc.Objects.AddPolyline(ToPoints(line, close: false), OnLayer(TerrainCurveExtractionStyle.Creases, "Crease")));

            foreach (TerrainCurveRecovery.RecoveredContour contour in recovery.Contours)
            {
                string contourName = $"Contour {contour.Level.ToString("F3", CultureInfo.InvariantCulture)}";
                using Curve curve = TerrainCurveExtractionGeometry.Contour(contour);
                ids.Add(doc.Objects.AddCurve(curve, OnLayer(TerrainCurveExtractionStyle.Contours, contourName)));
            }

            for (int i = 0; i < recovery.SpotPoints.Length; i += 3)
            {
                var point = new Point3d(recovery.SpotPoints[i], recovery.SpotPoints[i + 1], recovery.SpotPoints[i + 2]);
                ids.Add(doc.Objects.AddPoint(point, OnLayer(TerrainCurveExtractionStyle.Spots, "Spot")));
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

    /// <summary>
    /// The layer at <paramref name="relativePath"/> under <paramref name="root"/>, creating every missing level.
    /// A new layer takes the shipped template's colour and print width for its path, or its nearest listed
    /// ancestor's, so the scan's input layers look like the ones a terrain creates (<c>Input::Zones::Paved</c>
    /// takes <c>Input::Zones</c>'s).
    /// </summary>
    private static int EnsureLayer(RhinoDoc doc, string root, string relativePath, IReadOnlyList<LayerTemplateEntry> template)
    {
        string path = root;
        int index = doc.Layers.FindByFullPath(path, -1);
        if (index < 0)
            index = doc.Layers.Add(new Layer { Name = root });
        foreach (string segment in relativePath.Split("::"))
        {
            int parent = index;
            path += "::" + segment;
            index = doc.Layers.FindByFullPath(path, -1);
            if (index >= 0)
                continue;
            var layer = new Layer { Name = segment, ParentLayerId = doc.Layers[parent].Id };
            if (TemplateEntry(template, path[(root.Length + 2)..]) is { } entry)
            {
                if (entry.ColorArgb is int argb) layer.Color = System.Drawing.Color.FromArgb(argb);
                if (entry.PlotWeight is double weight) layer.PlotWeight = weight;
            }
            index = doc.Layers.Add(layer);
        }
        return index;
    }

    private static LayerTemplateEntry? TemplateEntry(IReadOnlyList<LayerTemplateEntry> template, string relativePath)
    {
        for (string path = relativePath; path.Length > 0; path = path.Contains("::", StringComparison.Ordinal) ? path[..path.LastIndexOf("::", StringComparison.Ordinal)] : string.Empty)
        {
            string full = TerrainLayerNaming.DefaultRoot + "::" + path;
            if (template.FirstOrDefault(entry => string.Equals(entry.Path, full, StringComparison.OrdinalIgnoreCase)) is { } entry)
                return entry;
        }
        return null;
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
