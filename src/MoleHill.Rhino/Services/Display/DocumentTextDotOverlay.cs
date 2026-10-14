using System.Drawing;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Redraws the document's own text dots over the terrain preview.
///
/// <para>Rhino draws a text dot without depth, in its object pass, before any conduit's PostDrawObjects. So
/// the preview mesh, drawn in PostDrawObjects, paints straight over every dot on the terrain — even a dot
/// floating above it, and even one sent to the front with BringToFront, since display order only orders
/// Rhino's own pass. A baked terrain is a Rhino mesh and never hides a dot; the preview must not either.</para>
///
/// <para>Drawing the mesh earlier is not the fix: in PreDrawObjects the dots and depth come out right, but
/// the viewport's lights are not yet set up and the terrain shades markedly darker than its bake (both
/// measured live). Dots are screen-space labels with no depth, so drawing them again after the preview
/// reproduces exactly what Rhino would show had the terrain been a document object.</para>
/// </summary>
internal static class DocumentTextDotOverlay
{
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, RhinoObject[]> DotsByDocument = new();

    static DocumentTextDotOverlay()
    {
        RhinoDoc.AddRhinoObject += OnObjectChanged;
        RhinoDoc.DeleteRhinoObject += OnObjectChanged;
        RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
        RhinoDoc.ReplaceRhinoObject += OnReplaceObject;
        RhinoDoc.ModifyObjectAttributes += OnModifyAttributes;
        RhinoDoc.CloseDocument += OnCloseDocument;
    }

    /// <summary>Draws every visible text dot in the view, in display order, as Rhino draws it.</summary>
    public static void Draw(DrawEventArgs e, RhinoDoc doc)
    {
        RhinoObject[] dots = GetDots(doc);
        if (dots.Length == 0)
            return;

        Guid viewportId = e.Viewport.Id;
        Color selectedColor = AppearanceSettings.SelectedObjectColor;
        foreach (RhinoObject dotObject in dots)
        {
            if (dotObject.IsDeleted || !dotObject.Visible || dotObject.Geometry is not TextDot dot)
                continue;

            int layerIndex = dotObject.Attributes.LayerIndex;
            if (layerIndex >= 0 && layerIndex < doc.Layers.Count && !doc.Layers[layerIndex].IsVisible)
                continue;

            if (!e.Viewport.IsVisible(dot.Point))
                continue;

            Color fill = dotObject.IsSelected(false) > 0
                ? selectedColor
                : dotObject.Attributes.DrawColor(doc, viewportId);
            Color text = fill.GetBrightness() > 0.5f ? Color.Black : Color.White;
            e.Display.DrawDot(dot, fill, text, text);
        }
    }

    private static RhinoObject[] GetDots(RhinoDoc doc)
    {
        lock (Gate)
        {
            if (DotsByDocument.TryGetValue(doc.RuntimeSerialNumber, out RhinoObject[]? cached))
                return cached;
        }

        var settings = new ObjectEnumeratorSettings
        {
            ObjectTypeFilter = ObjectType.TextDot,
            NormalObjects = true,
            LockedObjects = true,
            // Showing a hidden dot raises no event this list listens to, so hidden ones stay listed and
            // are skipped at draw time.
            HiddenObjects = true,
            ReferenceObjects = true,
            IncludeLights = false,
            IncludeGrips = false
        };

        // Stable by display order, so a dot sent to the front still lands on top of its neighbours.
        RhinoObject[] dots = doc.Objects.GetObjectList(settings)
            .OrderBy(dot => dot.Attributes.DisplayOrder)
            .ToArray();

        lock (Gate)
        {
            DotsByDocument[doc.RuntimeSerialNumber] = dots;
        }

        return dots;
    }

    private static void Invalidate(RhinoDoc? doc)
    {
        if (doc == null)
            return;

        lock (Gate)
        {
            DotsByDocument.Remove(doc.RuntimeSerialNumber);
        }
    }

    private static void OnObjectChanged(object? sender, RhinoObjectEventArgs e)
    {
        if (e.TheObject?.ObjectType == ObjectType.TextDot)
            Invalidate(e.TheObject.Document);
    }

    private static void OnReplaceObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        if (e.OldRhinoObject?.ObjectType == ObjectType.TextDot || e.NewRhinoObject?.ObjectType == ObjectType.TextDot)
            Invalidate(e.Document);
    }

    // Display order lives on the attributes.
    private static void OnModifyAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
        if (e.RhinoObject?.ObjectType == ObjectType.TextDot)
            Invalidate(e.Document);
    }

    private static void OnCloseDocument(object? sender, DocumentEventArgs e) => Invalidate(e.Document);
}
