using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Opens a block definition in Rhino's in-place block editor by name.
///
/// Rhino has no "edit this definition" entry point: <c>-BlockEdit</c> asks for an option and then for a
/// block <em>instance</em> picked in a viewport, and it will not take a name (verified live on 8.35 — a
/// typed name is read as an unknown command). So this selects an instance first: a baked one when the
/// document has one the user can reach, otherwise a temporary instance at the view's centre that is
/// removed once the block-edit session closes (OK runs <c>BlockEditApplyInPlaceEditItemChanges</c>;
/// every way out runs a <c>BlockEdit…</c> command).
/// </summary>
internal static class AnnotationBlockEditor
{
    private const string TemporaryInstanceKey = "MoleHill.BlockEditInstance";

    /// <summary>Returns false, with a reason, when the block could not be opened.</summary>
    public static bool TryOpen(RhinoDoc doc, string definitionName, out string? failure)
    {
        failure = null;
        InstanceDefinition? definition = doc.InstanceDefinitions.Find(definitionName);
        if (definition == null || definition.IsDeleted)
        {
            failure = $"there is no block named \"{definitionName}\" in this document to edit.";
            return false;
        }

        Guid temporaryId = Guid.Empty;
        RhinoObject? instance = FindEditableInstance(definition);
        if (instance == null)
        {
            temporaryId = AddTemporaryInstance(doc, definition);
            instance = temporaryId == Guid.Empty ? null : doc.Objects.FindId(temporaryId);
            if (instance == null)
            {
                failure = "Rhino would not place an instance of the block to edit.";
                return false;
            }
        }

        doc.Objects.UnselectAll();
        doc.Objects.Select(instance.Id, true, true);
        FrameInstance(doc, instance);

        // Watch from before the command runs: the first BlockEdit command to end is the one opening
        // the session, and the next BlockEdit… command to end is the one closing it.
        if (temporaryId != Guid.Empty)
            RemoveWhenSessionCloses(doc, temporaryId);

        RhinoApp.RunScript("_-BlockEdit _Open", false);
        return true;
    }

    /// <summary>A baked instance the user can see and select, so editing happens where the label is.</summary>
    private static RhinoObject? FindEditableInstance(InstanceDefinition definition)
    {
        foreach (InstanceObject reference in definition.GetReferences(0))
        {
            if (reference.IsDeleted || reference.Attributes.GetUserString(TemporaryInstanceKey) != null)
                continue;
            if (reference.IsSelectable(true, false, false, false))
                return reference;
        }

        return null;
    }

    private static Guid AddTemporaryInstance(RhinoDoc doc, InstanceDefinition definition)
    {
        Point3d target = doc.Views.ActiveView?.ActiveViewport.CameraTarget ?? Point3d.Origin;
        var attributes = doc.CreateDefaultAttributes();
        attributes.SetUserString(TemporaryInstanceKey, definition.Id.ToString());
        return doc.Objects.AddInstanceObject(definition.Index, Transform.Translation(target - Point3d.Origin), attributes);
    }

    /// <summary>
    /// Zooms so the block fills a useful part of the view. Annotation blocks are drawn at unit size, so in
    /// a site-scale model the block would otherwise open as a speck the user cannot find.
    /// </summary>
    private static void FrameInstance(RhinoDoc doc, RhinoObject instance)
    {
        var view = doc.Views.ActiveView;
        BoundingBox box = instance.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (view == null || !box.IsValid)
            return;

        double pad = Math.Max(box.Diagonal.Length, doc.ModelAbsoluteTolerance * 10.0) * 1.5;
        box.Inflate(pad);
        view.ActiveViewport.ZoomBoundingBox(box);
        view.Redraw();
    }

    private static void RemoveWhenSessionCloses(RhinoDoc doc, Guid temporaryId)
    {
        uint documentSerial = doc.RuntimeSerialNumber;
        bool sessionOpened = false;
        EventHandler<CommandEventArgs>? onEnd = null;
        onEnd = (_, e) =>
        {
            string name = e.CommandEnglishName ?? string.Empty;
            if (!name.StartsWith("BlockEdit", StringComparison.OrdinalIgnoreCase))
                return;

            // The opening -BlockEdit itself. If it was cancelled, no session follows: clean up now.
            if (!sessionOpened && string.Equals(name, "BlockEdit", StringComparison.OrdinalIgnoreCase))
            {
                sessionOpened = e.CommandResult == Result.Success;
                if (sessionOpened)
                    return;
            }

            Command.EndCommand -= onEnd;
            ScheduleRemoval(documentSerial, temporaryId);
        };
        Command.EndCommand += onEnd;
    }

    /// <summary>Deletes on idle, outside the ending command, so the removal is its own clean undo step.</summary>
    private static void ScheduleRemoval(uint documentSerial, Guid temporaryId)
    {
        EventHandler? onIdle = null;
        onIdle = (_, _) =>
        {
            RhinoApp.Idle -= onIdle;
            RhinoDoc? doc = RhinoDoc.FromRuntimeSerialNumber(documentSerial);
            if (doc?.Objects.FindId(temporaryId) is { } temporary &&
                temporary.Attributes.GetUserString(TemporaryInstanceKey) != null)
            {
                doc.Objects.Delete(temporary.Id, true);
                doc.Views.Redraw();
            }
        };
        RhinoApp.Idle += onIdle;
    }
}
