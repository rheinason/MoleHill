using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.UI;

// Reusable building blocks for the collapsible "card" UI shared by the modifier, zone, and object
// stacks. Each stack card has the same anatomy — a collapse chevron, a drag handle, an enable
// checkbox, a title block (name + subtitle when expanded, name + summary when collapsed), and a
// ctrl-collapses-all toggle — differing only in which model collection and collapsed-id set it is
// bound to. These helpers capture that shared anatomy so a new card type is a few lines of binding
// rather than a copy of the whole header.
public sealed partial class MoleHillPanel
{
    /// <summary>The expand/collapse chevron shown at the left of every stack card header.</summary>
    private static Label CreateCollapseChevron(bool collapsed) => new()
    {
        Text = collapsed ? "▶" : "▼",
        VerticalAlignment = VerticalAlignment.Center,
        Width = 14
    };

    /// <summary>
    /// The two-line title block used by every stack card: when collapsed, a bold read-only name over
    /// a summary line; when expanded, the editable <paramref name="nameBox"/> over a subtitle line.
    /// </summary>
    private static StackLayout CreateCardTitleBlock(
        bool collapsed,
        string collapsedName,
        string collapsedSummary,
        TextBox nameBox,
        string expandedSubtitle)
    {
        if (collapsed)
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                Items =
                {
                    new Label
                    {
                        Text = collapsedName,
                        Font = new Font(SystemFont.Bold),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    CreateCardMetaLabel(collapsedSummary)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Items =
            {
                nameBox,
                CreateCardMetaLabel(expandedSubtitle)
            }
        };
    }

    /// <summary>
    /// A drag handle wired to start a reorder drag carrying <paramref name="id"/> under the given
    /// <paramref name="dragKey"/> data format (the format the matching drop target listens for).
    /// </summary>
    private Drawable CreateReorderHandle(Guid id, string dragKey, string help)
    {
        var handle = CreateDragHandle();
        ApplyHelp(handle, help);
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            var data = new DataObject();
            data.SetString(id.ToString(), dragKey);
            handle.DoDragDrop(data, DragEffects.Move);
        };
        return handle;
    }

    /// <summary>A non-reorderable drag handle placeholder (greyed out) with an explanatory tooltip.</summary>
    private Drawable CreateDisabledReorderHandle(string help)
    {
        var handle = CreateDragHandle();
        handle.Enabled = false;
        handle.Cursor = Cursors.Default;
        ApplyHelp(handle, help);
        return handle;
    }

    /// <summary>
    /// Toggles the collapsed state of a single stack card, or — when <paramref name="ctrlHeld"/> is
    /// true — collapses every sibling card (or expands them all when the clicked card was the last
    /// collapsed one). Centralises the ctrl-collapse-all behaviour that all three stacks share.
    /// </summary>
    private void ToggleCardCollapsed(
        HashSet<Guid> collapsedIds,
        Guid id,
        bool ctrlHeld,
        Func<TerrainDefinition, IEnumerable<Guid>> siblingIds,
        Action<TerrainDefinition?> rebuild)
    {
        bool nowCollapsed = !collapsedIds.Contains(id);
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);

        if (ctrlHeld)
        {
            if (terrain != null)
            {
                if (nowCollapsed)
                {
                    foreach (var siblingId in siblingIds(terrain))
                        collapsedIds.Add(siblingId);
                }
                else
                {
                    collapsedIds.Clear();
                }
            }
        }
        else if (nowCollapsed)
        {
            collapsedIds.Add(id);
        }
        else
        {
            collapsedIds.Remove(id);
        }

        rebuild(terrain);
    }
}
